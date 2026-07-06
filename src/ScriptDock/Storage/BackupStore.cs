using System;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using ScriptDock.Services;

namespace ScriptDock.Storage;

/// <summary>
/// The write-through data-backup store (data-backup conventions). It owns one add-only SQLite file,
/// <c>backups.sqlite3</c>, directly under ScriptDock's storage root (<c>SCRIPTDOCK_HOME</c> or
/// <c>~/.scriptdock</c>, resolved in one place by <see cref="StorageRoot"/> — never a hardcoded path).
/// Every managed <em>text</em> save records the exact bytes it just wrote here, strictly AFTER its atomic
/// rename lands (see <see cref="JsonStore{T}"/>), so the history is always as current as the last save.
/// There is no startup scan, no periodic pass, no restore path.
/// </summary>
/// <remarks>
/// SQLite binding: <c>Microsoft.Data.Sqlite</c>, a managed ADO.NET provider that bundles native SQLite
/// through SQLitePCLRaw — no separate native rebuild step, which is what a record-after-rename hook wants.
/// A BLOB is read back as a <see cref="byte"/>[] and compared/hashed byte-for-byte here.
///
/// <para>Two absolute musts drive every line below (they are not best-effort aspirations):</para>
/// <list type="bullet">
///   <item>It never breaks a save and never crashes the app. The save has already succeeded — the file
///   is on disk before <see cref="Record"/> is called — so any failure here (the DB is locked, the disk
///   is full, an insert throws) is caught, logged once at <c>warn</c>, and swallowed. A lost record
///   self-heals on the next save of that file, whose content will differ from the last recorded row.</item>
///   <item>It logs only failures. A successful record logs NOTHING; a line per save would flood the log.</item>
/// </list>
/// </remarks>
public static class BackupStore
{
    /// <summary>The store's file name under the resolved storage root.</summary>
    public const string FileName = "backups.sqlite3";

    /// <summary>
    /// The one add-only table. <c>content</c> is a BLOB of the exact bytes written — never decoded text,
    /// so CR/LF, a BOM, and non-UTF-8 bytes are stored byte-identically. <c>written_at_utc</c> is the
    /// serialized ISO-8601-ms form (<c>2026-07-06T04:05:12.345Z</c>), a data value — NEVER the
    /// <c>yyyymmdd-hhmmss-fff-utc</c> filename stamp. The <c>(path, id)</c> index serves the
    /// latest-row-per-path dedup lookup.
    /// </summary>
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS backups (
          id             INTEGER PRIMARY KEY,
          path           TEXT NOT NULL,
          content        BLOB NOT NULL,
          content_sha256 TEXT NOT NULL,
          byte_size      INTEGER NOT NULL,
          written_at_utc TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_backups_path_id ON backups (path, id);
        """;

    private static readonly object Gate = new();

    // A null connection after Initialize means recording is disabled for this session because the store
    // could not be opened — a single warn was already logged; every later Record becomes a no-op rather
    // than retrying (and re-logging) a broken open on every save.
    private static SqliteConnection? _connection;
    private static bool _initialized;

    /// <summary>The store file under the resolved storage root. Computed on each open (not frozen into a
    /// constant at type-load) so <c>SCRIPTDOCK_HOME</c> is read after the environment is set, per the
    /// storage-path convention's caution against import-time resolution.</summary>
    public static string StoreFile => Path.Combine(StorageRoot.Directory, FileName);

    /// <summary>
    /// Open and initialize the store once (create the table if absent, switch on WAL and a busy timeout).
    /// Best-effort: on any failure it logs ONE warn, leaves recording disabled for the session, and never
    /// throws. WAL is what lets the tolerated two-instance case (two ScriptDock windows writing at once)
    /// serialize safely without a cross-process lock.
    /// </summary>
    private static SqliteConnection? EnsureOpen()
    {
        if (_initialized)
            return _connection;
        _initialized = true;

        try
        {
            var file = StoreFile;
            // The first writer under the root does the mkdir -p (storage-path convention); the store may
            // be the first thing written on a fresh root.
            // not recorded: backups.sqlite3 is the store itself — binary, and written by this backup layer,
            // not through the managed-text atomic-write path — so it never records itself. No recursion, no
            // special case (data-backup conventions: "A binary store, excluded from itself").
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);

            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = file,
                Mode = SqliteOpenMode.ReadWriteCreate,
            }.ToString());
            connection.Open();

            using (var pragmas = connection.CreateCommand())
            {
                pragmas.CommandText = "PRAGMA journal_mode = WAL;";
                pragmas.ExecuteNonQuery();
                // busy_timeout: under the tolerated two-instance case, a contended write waits up to this
                // long for SQLite's write lock instead of immediately failing with SQLITE_BUSY and dropping
                // that record.
                pragmas.CommandText = "PRAGMA busy_timeout = 5000;";
                pragmas.ExecuteNonQuery();
            }

            using (var schema = connection.CreateCommand())
            {
                schema.CommandText = Schema;
                schema.ExecuteNonQuery();
            }

            _connection = connection;
        }
        catch (Exception ex)
        {
            Log.Warn("backup store: could not open; recording disabled for this session", ex,
                new { file = StoreFile });
            _connection = null;
        }

        return _connection;
    }

    /// <summary>
    /// Record one managed-text write: <paramref name="absolutePath"/> is the FULL absolute path of the
    /// file as written; <paramref name="bytes"/> is the exact raw bytes just written (the caller already
    /// holds them — never re-read the file).
    ///
    /// <para>Dedup by content hash per path: the new content's SHA-256 is compared against the latest row
    /// for the same <c>path</c>, and the insert is SKIPPED when they are equal. This collapses consecutive
    /// identical saves (an autosave with no real change writes no row) while still recording every
    /// genuinely distinct version — including a revert, whose content differs from the immediately
    /// preceding row.</para>
    ///
    /// <para>Best-effort and silent on success; any failure is caught, logged once at <c>warn</c> (file +
    /// reason), and swallowed. It never throws, never crashes the app, and never breaks the save.</para>
    /// </summary>
    public static void Record(string absolutePath, byte[] bytes)
    {
        lock (Gate)
        {
            var connection = EnsureOpen();
            if (connection is null)
                return; // open failed earlier; disabled for the session (already warned once)

            try
            {
                var hash = Sha256Hex(bytes);

                using (var latest = connection.CreateCommand())
                {
                    latest.CommandText =
                        "SELECT content_sha256 FROM backups WHERE path = $path ORDER BY id DESC LIMIT 1";
                    latest.Parameters.AddWithValue("$path", absolutePath);
                    if (latest.ExecuteScalar() is string previousHash &&
                        string.Equals(previousHash, hash, StringComparison.Ordinal))
                    {
                        return; // unchanged since the last recorded version — dedup skip
                    }
                }

                using (var insert = connection.CreateCommand())
                {
                    insert.CommandText =
                        "INSERT INTO backups (path, content, content_sha256, byte_size, written_at_utc) " +
                        "VALUES ($path, $content, $hash, $size, $writtenAt)";
                    insert.Parameters.AddWithValue("$path", absolutePath);
                    insert.Parameters.AddWithValue("$content", bytes);
                    insert.Parameters.AddWithValue("$hash", hash);
                    insert.Parameters.AddWithValue("$size", bytes.LongLength);
                    // The serialized ISO-8601-ms data value (2026-07-06T04:05:12.345Z), NOT the filename
                    // stamp — per the timestamp conventions.
                    insert.Parameters.AddWithValue("$writtenAt", TimestampConventions.IsoMillis(DateTimeOffset.UtcNow));
                    insert.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                Log.Warn("backup store: failed to record a managed write", ex, new { file = absolutePath });
            }
        }
    }

    /// <summary>Close the store (best-effort). For tests that need to release the file handle between
    /// throwaway roots; the app itself lets the process exit close it. Resets the singleton so the next
    /// <see cref="Record"/> re-opens against the current <c>SCRIPTDOCK_HOME</c>.</summary>
    public static void Close()
    {
        lock (Gate)
        {
            try
            {
                _connection?.Close();
                _connection?.Dispose();
            }
            catch
            {
                // best-effort: a close failure on shutdown/teardown is harmless.
            }

            _connection = null;
            _initialized = false;
        }
    }

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));
}
