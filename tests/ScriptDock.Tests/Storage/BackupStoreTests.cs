using System;
using System.Linq.Expressions;
using System.Reflection;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Data.Sqlite;
using ScriptDock;
using ScriptDock.Models;
using ScriptDock.Storage;
using Xunit;

namespace ScriptDock.Tests.Storage;

/// <summary>
/// Pins the backup history (<see cref="BackupStore"/>) against a throwaway <c>SCRIPTDOCK_DATA_DIR</c>, on a
/// real SQLite file, since the point is a byte-identical copy. Locked here: the <c>content</c> BLOB is the
/// exact bytes; <c>written_at_utc</c> is the serialized ISO-8601-ms value; each path keeps one row per
/// session, replaced by later saves, and a session's first save of content already held writes nothing;
/// a save never waits for the history; and an unusable store neither throws nor breaks the save.
/// </summary>
[Collection(StorageRootEnvironment.CollectionName)]
public sealed class BackupStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string? _previousHome;

    public BackupStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "scriptdock-tests", NanoId.New());
        Directory.CreateDirectory(_root);

        _previousHome = Environment.GetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable);
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, _root);
    }

    public void Dispose()
    {
        // Release the singleton's handle on this throwaway root's store before the directory is deleted,
        // and reset it so the next test re-opens against its own SCRIPTDOCK_DATA_DIR.
        BackupStore.Close();
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, _previousHome);
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private string StoreFile => Path.Combine(_root, BackupStore.FileName);

    private sealed record Row(string Path, byte[] Content, string Sha256, long ByteSize, string WrittenAtUtc, string? Session);

    private static void Post(Action work)
    {
        var post = typeof(BackupStore).GetMethod("Post", BindingFlags.Static | BindingFlags.NonPublic)!;
        var parameter = Expression.Parameter(post.GetParameters()[0].ParameterType.GenericTypeArguments[0]);
        var queued = Expression.Lambda(post.GetParameters()[0].ParameterType,
            Expression.Invoke(Expression.Constant(work)), parameter).Compile();
        post.Invoke(null, [queued]);
    }

    /// <summary>Reads every recorded row for a path, oldest first — a direct DB read so the assertion does
    /// not depend on any public read API the feature deliberately does not expose.</summary>
    private List<Row> RowsFor(string path)
    {
        BackupStore.Flush();
        var rows = new List<Row>();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = StoreFile,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT path, content, content_sha256, byte_size, written_at_utc, session_id " +
            "FROM backups WHERE path = $path ORDER BY id ASC";
        command.Parameters.AddWithValue("$path", path);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var content = (byte[])reader["content"];
            rows.Add(new Row(
                reader.GetString(0),
                content,
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return rows;
    }

    [Fact]
    public void Record_StoresContentByteIdentical_IncludingCrLfAndNonUtf8()
    {
        var path = Path.Combine(_root, "doc.json");
        // A CRLF, a UTF-8 BOM, and a raw 0xFF byte that is not valid UTF-8: if the store decoded to text
        // anywhere, the CRLF would normalize, the BOM would drop or shift, and 0xFF would corrupt.
        byte[] bytes = [0xEF, 0xBB, 0xBF, (byte)'a', (byte)'\r', (byte)'\n', (byte)'b', 0xFF, 0x00, (byte)'c'];

        BackupStore.Record(path, bytes);

        var rows = RowsFor(path);
        Assert.Single(rows);
        Assert.Equal(bytes, rows[0].Content); // byte-for-byte, no normalization
        Assert.Equal(bytes.LongLength, rows[0].ByteSize);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), rows[0].Sha256);
        Assert.Equal(path, rows[0].Path);
    }

    [Fact]
    public void Record_WrittenAtUtc_IsSerializedIsoMillis_NotAFilenameStamp()
    {
        var path = Path.Combine(_root, "doc.json");
        BackupStore.Record(path, [1, 2, 3]);

        var writtenAt = RowsFor(path)[0].WrittenAtUtc;

        // The serialized ISO-8601-ms shape: 2026-07-06T04:05:12.345Z.
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", writtenAt);
        // And explicitly NOT the yyyymmdd-hhmmss-fff-utc filename stamp.
        Assert.DoesNotMatch(@"^\d{8}-\d{6}-\d{3}-utc$", writtenAt);
        Assert.DoesNotContain("-utc", writtenAt);
        // It round-trips as a real instant.
        Assert.True(DateTimeOffset.TryParse(writtenAt, out _));
    }

    [Fact]
    public void Record_InOneSession_KeepsOneRowWithTheLatestContent()
    {
        var path = Path.Combine(_root, "doc.json");
        BackupStore.Session = "s1";

        BackupStore.Record(path, [1]);
        BackupStore.Record(path, [1, 2]);
        BackupStore.Record(path, [1, 2, 3]);

        var row = Assert.Single(RowsFor(path));
        Assert.Equal([1, 2, 3], row.Content);
        Assert.Equal("s1", row.Session);
    }

    [Fact]
    public void Record_QueuedSavesKeepTheSaveInstantAndLatestContent()
    {
        var path = Path.Combine(_root, "doc.json");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Post(() => { entered.Set(); release.Wait(); });
        DateTimeOffset before;
        DateTimeOffset after;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            before = DateTimeOffset.UtcNow;
            BackupStore.Record(path, [1]);
            BackupStore.Record(path, [2]);
            after = DateTimeOffset.UtcNow;
            // Advance beyond the serialized millisecond before allowing the worker to dequeue.
            Assert.True(SpinWait.SpinUntil(() => DateTimeOffset.UtcNow > after.AddMilliseconds(2),
                TimeSpan.FromSeconds(5)));
        }
        finally
        {
            release.Set();
        }

        var row = Assert.Single(RowsFor(path));
        Assert.Equal(new byte[] { 2 }, row.Content);
        var writtenAt = DateTimeOffset.Parse(row.WrittenAtUtc);
        Assert.InRange(writtenAt, before.AddMilliseconds(-1), after);
    }

    [Fact]
    public void Record_EachSession_AddsItsOwnRow_AndLeavesEarlierSessionsAlone()
    {
        var path = Path.Combine(_root, "doc.json");
        BackupStore.Session = "s1";
        BackupStore.Record(path, [1]);
        BackupStore.Session = "s2";
        BackupStore.Record(path, [2]);
        BackupStore.Record(path, [3]);

        var rows = RowsFor(path);
        Assert.Equal(["s1", "s2"], rows.ConvertAll(row => row.Session));
        Assert.Equal([1], rows[0].Content);
        Assert.Equal([3], rows[1].Content);
    }

    [Fact]
    public void Record_SessionsFirstSaveOfContentAlreadyHeld_WritesNothing()
    {
        var path = Path.Combine(_root, "doc.json");
        BackupStore.Session = "s1";
        BackupStore.Record(path, [7, 7]);
        BackupStore.Session = "s2";
        BackupStore.Record(path, [7, 7]);

        Assert.Equal("s1", Assert.Single(RowsFor(path)).Session);
    }

    [Fact]
    public void Record_KeepsEachPathApart()
    {
        var a = Path.Combine(_root, "a.json");
        var b = Path.Combine(_root, "b.json");

        BackupStore.Record(a, [7, 7, 7]);
        BackupStore.Record(b, [7, 7, 7]);

        Assert.Single(RowsFor(a));
        Assert.Single(RowsFor(b));
    }

    public sealed class SampleDoc
    {
        public string Name { get; set; } = "";
    }

    [Fact]
    public async Task JsonStoreSave_RecordsThroughTheChokePoint_AfterTheRenameLands()
    {
        // The end-to-end wire: a managed-text save through JsonStore's single atomic-write choke point
        // records the exact bytes it wrote, at the file's full absolute path, strictly after the rename.
        var store = new JsonStore<SampleDoc>("doc.json", "doc", formatVersion: 1);
        var docPath = Path.Combine(_root, "doc.json");

        await store.SaveAsync(new SampleDoc { Name = "one" });

        var onDisk = File.ReadAllBytes(docPath);
        var rows = RowsFor(docPath);
        Assert.Single(rows);
        Assert.Equal(onDisk, rows[0].Content); // recorded bytes are byte-identical to what landed on disk
        Assert.Equal(docPath, rows[0].Path);   // full absolute path
    }

    [Fact]
    public async Task JsonStoreSave_OptedOutStatePersistsWithoutCreatingOrAddingToHistory()
    {
        var statePath = Path.Combine(_root, AppPaths.StateFileName);
        var stateStore = AppStores.State();
        var state = new AppState { WindowWidth = 1100, WindowHeight = 760 };
        await stateStore.SaveAsync(state);
        Assert.False(File.Exists(StoreFile));
        Assert.Equal(1100, stateStore.Load().WindowWidth);

        var configStore = new ConfigStore();
        var config = configStore.Load();
        config.Hidden = ["/hidden.command"];
        await configStore.SaveAsync(config);

        state.WindowWidth = 1200;
        await stateStore.SaveAsync(state);
        Assert.Equal(1200, stateStore.Load().WindowWidth);
        Assert.Empty(RowsFor(statePath));
        var configPath = Path.Combine(_root, AppPaths.ConfigFileName);
        Assert.Equal(File.ReadAllBytes(configPath), Assert.Single(RowsFor(configPath)).Content);
    }

    [Fact]
    public async Task JsonStoreSave_KeepsTheSessionsLatestVersion()
    {
        var store = new JsonStore<SampleDoc>("doc.json", "doc", formatVersion: 1);
        var docPath = Path.Combine(_root, "doc.json");

        await store.SaveAsync(new SampleDoc { Name = "one" });
        await store.SaveAsync(new SampleDoc { Name = "two" });

        Assert.Equal(File.ReadAllBytes(docPath), Assert.Single(RowsFor(docPath)).Content);
    }

    [Fact]
    public async Task JsonStoreSave_DoesNotWaitForTheHistory()
    {
        // Another connection holds the history locked, so recording waits out its busy timeout; the save
        // still completes while the lock is held, and the row lands once the lock is gone.
        var store = new JsonStore<SampleDoc>("doc.json", "doc", formatVersion: 1);
        var docPath = Path.Combine(_root, "doc.json");
        await store.SaveAsync(new SampleDoc { Name = "one" });
        BackupStore.Flush();

        Task saving = Task.CompletedTask;
        try
        {
            using var holder = new SqliteConnection($"Data Source={StoreFile};Pooling=False");
            holder.Open();
            using (var lockIt = holder.CreateCommand())
            {
                lockIt.CommandText = "BEGIN EXCLUSIVE;";
                lockIt.ExecuteNonQuery();
            }

            // The deadline is only a deadlock watchdog: completion is observed while the separate
            // connection still owns its write lock, rather than asserting save performance.
            saving = Task.Run(() => store.SaveAsync(new SampleDoc { Name = "two" }), TestContext.Current.CancellationToken);
            await saving.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            using var query = holder.CreateCommand();
            query.CommandText = "SELECT content FROM backups WHERE path = $path";
            query.Parameters.AddWithValue("$path", docPath);
            Assert.NotEqual(File.ReadAllBytes(docPath), (byte[])query.ExecuteScalar()!);
        }
        finally
        {
            // Release the lock before settling a save whose watchdog failed, while the disposable
            // storage root is still installed.
            await saving;
        }

        Assert.Equal(File.ReadAllBytes(docPath), Assert.Single(RowsFor(docPath)).Content);
    }

    [Fact]
    public void Record_WhenStoreCannotOpen_DoesNotThrow_AndTheSaveIsUnaffected()
    {
        // Inject an open failure: put a *file* where the storage root's directory must be, so the store's
        // Directory.CreateDirectory / open cannot succeed against this SCRIPTDOCK_DATA_DIR.
        var blockedRoot = Path.Combine(Path.GetTempPath(), "scriptdock-tests", NanoId.New() + "-blocked");
        File.WriteAllText(blockedRoot, "not a directory");
        var previous = Environment.GetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable);
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, blockedRoot);
        BackupStore.Close(); // force a re-open against the blocked root

        try
        {
            // The record itself must be best-effort: swallow the failure, never throw, so a caller's save
            // (which has already landed on disk before Record is reached) is never broken by the backup.
            var exception = Record.Exception(() => BackupStore.Record(Path.Combine(blockedRoot, "doc.json"), [1, 2, 3]));
            Assert.Null(exception);

            // A second record after the open already failed is a silent no-op (disabled for the session),
            // not a re-throw and not a re-open attempt.
            Assert.Null(Record.Exception(() => BackupStore.Record(Path.Combine(blockedRoot, "doc.json"), [4, 5, 6])));
        }
        finally
        {
            BackupStore.Close();
            Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, previous);
            try { File.Delete(blockedRoot); } catch { /* best-effort */ }
        }
    }
}
