using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Data.Sqlite;
using ScriptDock.Services;

namespace ScriptDock.Storage;

/// <summary>
/// The backup history (data-backup-conventions): <c>backups.sqlite3</c> under the storage root, holding the
/// last version of each protected file saved in each session (one process launch). <c>config.json</c> (the
/// user's settings: scan folders, extensions, ignore patterns and the rest) and <c>hidden.json</c> (the
/// hidden scripts) are protected. Records, view state,
/// the remembered script list and logs are not (see <see cref="AppStores"/>). Restore is manual.
/// </summary>
/// <remarks>
/// <para>A save hands its exact bytes to <see cref="Record"/> after its rename lands and goes on: one owner
/// thread applies the recordings in the order they arrived, so a slow or failing history never delays a
/// save, fails it, or blocks other work. Any failure is logged once per write and swallowed; a store that
/// cannot be opened stays off for the session.</para>
/// <para>Within a session each path keeps one row, replaced by later saves. A session's first save whose
/// content equals the path's latest row writes nothing. Rows of earlier sessions are never changed.</para>
/// <para>The exit gives pending recordings what remains of its deadline (<see cref="Shutdown"/>); when the
/// operating system ends the session the exit does not wait for them.</para>
/// </remarks>
public static class BackupStore
{
    /// <summary>The store's file name under the resolved storage root.</summary>
    public const string FileName = "backups.sqlite3";

    /// <summary>
    /// <c>content</c> is a BLOB of the exact bytes written, never decoded text. <c>written_at_utc</c> is the
    /// serialized ISO-8601-ms time of the row's latest save. <c>session_id</c> is the session (one process
    /// launch) the row's save came from. The unique <c>(path, session_id)</c> index owns the
    /// one-row-per-session rule; <c>(path, id)</c> serves the latest-row lookup.
    /// </summary>
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS backups (
          id             INTEGER PRIMARY KEY,
          path           TEXT NOT NULL,
          content        BLOB NOT NULL,
          content_sha256 TEXT NOT NULL,
          byte_size      INTEGER NOT NULL,
          written_at_utc TEXT NOT NULL,
          session_id     TEXT
        );
        CREATE UNIQUE INDEX IF NOT EXISTS idx_backups_path_session ON backups (path, session_id);
        CREATE INDEX IF NOT EXISTS idx_backups_path_id ON backups (path, id);
        """;

    // One owner for the process: its thread applies the recordings in the order they arrived. A background
    // thread, so a recording never holds the process at exit.
    private static readonly BlockingCollection<Action<Connection>> Queue = new();
    private static readonly Thread Owner = StartOwner();

    // The owner's connection, opened on its first recording. Null after that open means the store is off
    // for the session; one warning was logged.
    private sealed class Connection
    {
        public bool Opened;
        public SqliteConnection? Value;
    }

    /// <summary>This session's id. The app sets it to the records' session at startup, which also starts the
    /// owner, so a history row and the run records of the same launch share it.</summary>
    public static string Session { get; set; } = TimestampConventions.IsoMillis(DateTimeOffset.UtcNow);

    /// <summary>The clock a recording's save instant comes from; tests set a fake one.</summary>
    internal static TimeProvider Time { get; set; } = TimeProvider.System;

    /// <summary>The store file under the resolved storage root.</summary>
    public static string StoreFile => Path.Combine(StorageRoot.Directory, FileName);

    /// <summary>
    /// Queues one protected write: <paramref name="absolutePath"/> is the file's full absolute path and
    /// <paramref name="bytes"/> the exact bytes just written, which the caller no longer changes. Returns at
    /// once and never throws.
    /// </summary>
    public static void Record(string absolutePath, byte[] bytes)
    {
        var session = Session;
        var writtenAtUtc = Time.GetUtcNow();
        Post(connection => Write(connection, absolutePath, bytes, session, writtenAtUtc));
    }

    /// <summary>Waits until every recording queued before this call is applied. For tests.</summary>
    internal static void Flush()
    {
        using var done = new ManualResetEventSlim();
        if (Post(_ => done.Set()))
            done.Wait();
    }

    /// <summary>
    /// At exit: stops taking recordings and gives the pending ones up to <paramref name="bound"/>, what remains
    /// of the exit deadline, to land. Recordings still pending then are lost with the process; the files they
    /// came from are saved.
    /// </summary>
    public static void Shutdown(TimeSpan bound)
    {
        Queue.CompleteAdding();
        if (!Owner.Join(bound))
            Log.Warn("backup store: recordings still pending at the exit deadline", new { boundMs = bound.TotalMilliseconds });
    }

    /// <summary>Applies everything pending and closes the connection, so the next recording reopens the store
    /// against the current storage root. For tests.</summary>
    internal static void Close()
    {
        using var done = new ManualResetEventSlim();
        if (Post(connection =>
            {
                try { connection.Value?.Dispose(); }
                catch { /* closing between tests; nothing to report */ }
                connection.Value = null;
                connection.Opened = false;
                done.Set();
            }))
            done.Wait();
    }

    // False once the exit has closed the queue.
    private static bool Post(Action<Connection> work)
    {
        try
        {
            return Queue.TryAdd(work);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static Thread StartOwner()
    {
        var owner = new Thread(() =>
        {
            var connection = new Connection();
            foreach (var work in Queue.GetConsumingEnumerable())
                work(connection);
            try { connection.Value?.Dispose(); }
            catch { /* closing at exit; nothing to report */ }
        }) { IsBackground = true, Name = "backups" };
        owner.Start();
        return owner;
    }

    private static void Write(Connection owner, string absolutePath, byte[] bytes, string session, DateTimeOffset writtenAtUtc)
    {
        var connection = Open(owner);
        if (connection is null)
            return;

        try
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));

            // The latest row is this session's own when it has one, so an unchanged save writes nothing,
            // and so does a session's first save of content an earlier session already holds.
            using (var latest = connection.CreateCommand())
            {
                latest.CommandText = "SELECT content_sha256 FROM backups WHERE path = $path ORDER BY id DESC LIMIT 1";
                latest.Parameters.AddWithValue("$path", absolutePath);
                if (latest.ExecuteScalar() is string previous && string.Equals(previous, hash, StringComparison.Ordinal))
                    return;
            }

            using var upsert = connection.CreateCommand();
            upsert.CommandText =
                "INSERT INTO backups (session_id, path, content, content_sha256, byte_size, written_at_utc) " +
                "VALUES ($session, $path, $content, $hash, $size, $writtenAt) " +
                "ON CONFLICT (path, session_id) DO UPDATE SET content = excluded.content, " +
                "content_sha256 = excluded.content_sha256, byte_size = excluded.byte_size, " +
                "written_at_utc = excluded.written_at_utc";
            upsert.Parameters.AddWithValue("$session", session);
            upsert.Parameters.AddWithValue("$path", absolutePath);
            upsert.Parameters.AddWithValue("$content", bytes);
            upsert.Parameters.AddWithValue("$hash", hash);
            upsert.Parameters.AddWithValue("$size", bytes.LongLength);
            upsert.Parameters.AddWithValue("$writtenAt", TimestampConventions.IsoMillis(writtenAtUtc));
            upsert.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Log.Warn("backup store: failed to record a save", ex, new { file = absolutePath });
        }
    }

    // Opens the store on its first use in a session. A database a newer version wrote is left untouched and,
    // like any other open failure, turns recording off for the session.
    private static SqliteConnection? Open(Connection owner)
    {
        if (owner.Opened)
            return owner.Value;
        owner.Opened = true;

        SqliteConnection? connection = null;
        var file = StoreFile;
        try
        {
            // The store sits directly under the storage root and may be the first thing written to it.
            StorageRoot.EnsureExists();
            connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = file,
                Mode = SqliteOpenMode.ReadWriteCreate,
            }.ToString());
            // No busy timeout: this is the store's one connection in the app.
            connection.Open();

            // Before anything writes, the journal mode included.
            SqliteFormatVersion.Check(connection, file, FormatVersions.Backups);
            Execute(connection, "PRAGMA journal_mode = WAL;");
            Execute(connection, Schema);
            owner.Value = connection;
        }
        catch (NewerFormatVersionException ex)
        {
            connection?.Dispose();
            Log.Warn("backup store: written by a newer version; left in place, recording off for this session", ex,
                new { file, found = ex.Found, supported = ex.Supported });
        }
        catch (Exception ex)
        {
            // An unreadable version marker among them: left untouched, like a newer one.
            connection?.Dispose();
            Log.Warn("backup store: could not open; recording off for this session", ex, new { file });
        }

        return owner.Value;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
