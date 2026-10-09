using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ScriptDock;
using ScriptDock.Models;
using ScriptDock.Storage;
using Xunit;

namespace ScriptDock.Tests.Storage;

/// <summary>
/// Each store's format version (store-recovery-conventions): a store an earlier build wrote without its
/// marker reads as format 1, the current version round-trips, and a newer one is refused with the file left
/// byte-identical, except in the disposable stores, which rebuild instead.
/// </summary>
[Collection(StorageRootEnvironment.CollectionName)]
public sealed class FormatVersionTests : IDisposable
{
    private static readonly DateTimeOffset SessionStart = new(2026, 10, 5, 1, 2, 3, 456, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "scriptdock-tests", NanoId.New());
    private readonly string? _previousHome = Environment.GetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable);

    public FormatVersionTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, _root);
        QuarantineJournal.Drain();
    }

    public void Dispose()
    {
        BackupStore.Close();
        SqliteConnection.ClearAllPools();
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, _previousHome);
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private string PathOf(string fileName) => Path.Combine(_root, fileName);

    // config.json

    // v0.1.0 stored every setting, the built-in seeds and two since-retired process settings included.
    private const string V010Config = """
        {
          "uiFontFamily": "Inter",
          "rootDirs": ["/Users/me/scripts"],
          "extensions": [".command"],
          "ignorePatterns": ["/node_modules/", "/\\.venv/", "/venv/", "/__pycache__/", "/bin/", "/obj/", "/target/", "/\\.git/"],
          "hidden": ["/Users/me/scripts/old.command"],
          "killProcessesOnClose": false,
          "recaptureProcessesOnLaunch": true
        }
        """;

    [Fact]
    public async Task Config_FromV010WithoutMarker_KeepsItsSettings()
    {
        // v0.1.0 seeded the platform's own launcher extension.
        File.WriteAllText(PathOf(AppPaths.ConfigFileName),
            V010Config.Replace("\".command\"", $"\"{ConfigDefaults.DefaultExtension}\""));
        var store = new ConfigStore();

        var config = store.Load();
        Assert.Equal(["/Users/me/scripts"], config.RootDirs);
        Assert.Equal(["/Users/me/scripts/old.command"], config.Hidden);
        Assert.Equal([ConfigDefaults.DefaultExtension], config.Extensions);
        Assert.Equal(ConfigDefaults.BuiltInIgnorePatterns, config.IgnorePatterns);
        Assert.Empty(store.KeptKeys);
        Assert.Empty(Directory.GetFiles(_root, "*.invalid"));
        Assert.Empty(QuarantineJournal.Drain());

        // The seeds equal today's built-ins and the retired settings are dropped, so only real choices stay.
        await store.SaveAsync(config);
        using var saved = JsonDocument.Parse(File.ReadAllText(PathOf(AppPaths.ConfigFileName)));
        Assert.Equal(["formatVersion", "uiFontFamily", "rootDirs", "hidden"],
            saved.RootElement.EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public void Config_FromOctoberWithoutMarker_KeepsItsSettings()
    {
        File.WriteAllText(PathOf(AppPaths.ConfigFileName), """{"theme":"dark","hidden":["/a.command"]}""");

        var config = new ConfigStore().Load();

        Assert.Equal(ThemePreference.Dark, config.Theme);
        Assert.Equal(["/a.command"], config.Hidden);
        Assert.Empty(Directory.GetFiles(_root, "*.invalid"));
    }

    [Fact]
    public async Task Config_CurrentVersion_RoundTrips()
    {
        var store = new ConfigStore();
        var config = store.Load();
        config.Hidden = ["/a.command"];
        await store.SaveAsync(config);

        Assert.Equal(FormatVersions.Config, FirstKeyVersion(AppPaths.ConfigFileName));
        Assert.Equal(["/a.command"], store.Load().Hidden);
    }

    [Fact]
    public void Config_NewerVersion_IsRefusedAndLeftByteIdentical() =>
        AssertJsonRefused(AppPaths.ConfigFileName, FormatVersions.Config, () => new ConfigStore().Load());

    // state.json

    [Fact]
    public void State_WithoutMarker_ReadsAsFormatOne()
    {
        File.WriteAllText(PathOf(AppPaths.StateFileName), """{"windowWidth":900}""");

        Assert.Equal(900, AppStores.State().Load().WindowWidth);
        Assert.Empty(Directory.GetFiles(_root, "*.invalid"));
    }

    [Fact]
    public async Task State_CurrentVersion_RoundTrips()
    {
        var store = AppStores.State();
        await store.SaveAsync(new AppState { WindowWidth = 900 });

        Assert.Equal(FormatVersions.State, FirstKeyVersion(AppPaths.StateFileName));
        Assert.Equal(900, store.Load().WindowWidth);
    }

    [Fact]
    public async Task State_NewerVersion_IsRebuilt()
    {
        AssertJsonRebuilt(AppPaths.StateFileName, FormatVersions.State, () => AppStores.State().Load().WindowWidth);
        await AppStores.State().SaveAsync(new AppState { WindowWidth = 900 });
        Assert.Equal(FormatVersions.State, FirstKeyVersion(AppPaths.StateFileName));
    }

    // known-paths.json

    [Fact]
    public void KnownPaths_WithoutMarker_ReadsAsFormatOne()
    {
        File.WriteAllText(PathOf(AppPaths.KnownPathsFileName), """{"paths":["/a.command"]}""");

        Assert.Equal(["/a.command"], AppStores.KnownPaths().Load().Paths);
        Assert.Empty(Directory.GetFiles(_root, "*.invalid"));
    }

    [Theory]
    [InlineData("""["/a.command",null]""")]
    [InlineData("""["/a.command",1]""")]
    public void KnownPaths_WithAMemberThatIsNotAPath_AreNoBaseline(string paths)
    {
        File.WriteAllText(PathOf(AppPaths.KnownPathsFileName), $$"""{"formatVersion":1,"paths":{{paths}}}""");

        Assert.Null(AppStores.KnownPaths().Load().Paths);
        Assert.Empty(Directory.GetFiles(_root, "*.invalid"));
    }

    [Fact]
    public void KnownPaths_WithAnEmptyList_AreABaseline()
    {
        File.WriteAllText(PathOf(AppPaths.KnownPathsFileName), """{"formatVersion":1,"paths":[]}""");

        Assert.Equal([], AppStores.KnownPaths().Load().Paths!);
    }

    [Fact]
    public async Task KnownPaths_CurrentVersion_RoundTrips()
    {
        var store = AppStores.KnownPaths();
        await store.SaveAsync(new KnownPaths { Paths = ["/a.command"] });

        Assert.Equal(FormatVersions.KnownPaths, FirstKeyVersion(AppPaths.KnownPathsFileName));
        Assert.Equal(["/a.command"], store.Load().Paths);
    }

    [Fact]
    public void KnownPaths_NewerVersion_IsRebuilt() =>
        AssertJsonRebuilt(AppPaths.KnownPathsFileName, FormatVersions.KnownPaths, () => AppStores.KnownPaths().Load().Paths);

    // records.sqlite3

    // The schema builds from 2026-10-02 wrote without a version: every table but run_ends.
    private const string October2Records = """
        CREATE TABLE logs (id INTEGER PRIMARY KEY, session TEXT NOT NULL, time TEXT NOT NULL, level TEXT NOT NULL, message TEXT NOT NULL, line TEXT NOT NULL);
        CREATE INDEX idx_logs_session ON logs (session);
        CREATE TABLE runs (id INTEGER PRIMARY KEY, session TEXT NOT NULL, run INTEGER NOT NULL, time TEXT NOT NULL, script TEXT NOT NULL, pid INTEGER, os_started_at TEXT, output_path TEXT, UNIQUE (session, run));
        CREATE INDEX idx_runs_output_path ON runs (output_path);
        CREATE TABLE run_outputs (id INTEGER PRIMARY KEY, session TEXT NOT NULL, run INTEGER NOT NULL, time TEXT NOT NULL, output BLOB NOT NULL, UNIQUE (session, run));
        CREATE TABLE dismissals (id INTEGER PRIMARY KEY, session TEXT NOT NULL, time TEXT NOT NULL, script TEXT NOT NULL);
        CREATE TABLE scan_reports (id INTEGER PRIMARY KEY, session TEXT NOT NULL, time TEXT NOT NULL, report TEXT NOT NULL);
        INSERT INTO runs (session, run, time, script) VALUES ('2026-10-03T00:00:00.000Z', 1, '2026-10-03T00:00:00.000Z', '/old.command');
        """;

    [Fact]
    public async Task Records_FromOctoberWithoutVersion_OpenAndKeepTheirRuns()
    {
        var file = PathOf(RecordStore.FileName);
        Execute(file, October2Records);

        using (var records = new RecordStore(_root, SessionStart))
        {
            Assert.False(records.DatabaseUnavailable);
            Assert.Equal("/old.command", Assert.Single(await records.ReadRecentAsync()).Path);
            await records.AddRunEndAsync(new RunEnd("2026-10-03T00:00:00.000Z", 1, SessionStart, "exited", 0));
        }

        Assert.Equal(FormatVersions.Records, UserVersion(file));
    }

    [Fact]
    public async Task Records_WithoutVersionAndWithAnUnknownTable_AreUnreadableAndLeftAlone()
    {
        var file = PathOf(RecordStore.FileName);
        CreateDatabase(file, userVersion: 0);
        var before = File.ReadAllBytes(file);

        using (var records = new RecordStore(_root, SessionStart))
        {
            Assert.True(records.DatabaseUnavailable);
            await Assert.ThrowsAnyAsync<Exception>(() => records.AddDismissalAsync("/a.command"));
            Assert.True(File.Exists(records.FallbackPath));
        }

        SqliteConnection.ClearAllPools();
        Assert.Equal(before, File.ReadAllBytes(file));
    }

    [Fact]
    public async Task Records_CurrentVersion_RoundTrips()
    {
        using (var records = new RecordStore(_root, SessionStart))
            await records.AddRunAsync(new RunRecord("s", 1, SessionStart, "/a.command", null, null, null));

        Assert.Equal(FormatVersions.Records, UserVersion(PathOf(RecordStore.FileName)));
        using var reopened = new RecordStore(_root, SessionStart.AddMinutes(1));
        Assert.Equal("/a.command", Assert.Single(await reopened.ReadRecentAsync()).Path);
    }

    [Fact]
    public void Records_NewerVersion_IsRefusedAndLeftByteIdentical()
    {
        var file = PathOf(RecordStore.FileName);
        CreateDatabase(file, FormatVersions.Records + 1);
        var before = File.ReadAllBytes(file);

        var refused = Assert.Throws<NewerFormatVersionException>(() => new RecordStore(_root, SessionStart));

        Assert.Equal(file, refused.FilePath);
        SqliteConnection.ClearAllPools();
        Assert.Equal(before, File.ReadAllBytes(file));
    }

    // backups.sqlite3, a side store: a newer one disables recording for the session instead of stopping the app.

    [Fact]
    public void Backups_FromV010WithoutVersion_KeepTheirHistoryAndRecord()
    {
        Execute(BackupStore.StoreFile, """
            CREATE TABLE backups (id INTEGER PRIMARY KEY, path TEXT NOT NULL, content BLOB NOT NULL, content_sha256 TEXT NOT NULL, byte_size INTEGER NOT NULL, written_at_utc TEXT NOT NULL);
            CREATE INDEX idx_backups_path_id ON backups (path, id);
            INSERT INTO backups (path, content, content_sha256, byte_size, written_at_utc) VALUES ('/old.json', x'00', 'x', 1, '2026-07-08T00:00:00.000Z');
            """);

        BackupStore.Session = "now";
        BackupStore.Record(PathOf("doc.json"), [1]);
        BackupStore.Close();

        // Format 1's rows stay as earlier history, without a session.
        Assert.Equal(FormatVersions.Backups, UserVersion(BackupStore.StoreFile));
        Assert.Equal(1L, Scalar(BackupStore.StoreFile, "SELECT COUNT(*) FROM backups WHERE path = '/old.json' AND session_id IS NULL"));
        Assert.Equal(1L, Scalar(BackupStore.StoreFile, "SELECT COUNT(*) FROM backups WHERE session_id = 'now'"));
    }

    [Fact]
    public void Backups_AtFormatOne_GainSessionsAndKeepTheirRows()
    {
        Execute(BackupStore.StoreFile, """
            CREATE TABLE backups (id INTEGER PRIMARY KEY, path TEXT NOT NULL, content BLOB NOT NULL, content_sha256 TEXT NOT NULL, byte_size INTEGER NOT NULL, written_at_utc TEXT NOT NULL);
            CREATE INDEX idx_backups_path_id ON backups (path, id);
            INSERT INTO backups (path, content, content_sha256, byte_size, written_at_utc) VALUES ('/a.json', x'01', 'a', 1, '2026-10-06T00:00:00.000Z');
            INSERT INTO backups (path, content, content_sha256, byte_size, written_at_utc) VALUES ('/a.json', x'02', 'b', 1, '2026-10-07T00:00:00.000Z');
            PRAGMA user_version = 1;
            """);

        BackupStore.Session = "now";
        BackupStore.Record("/a.json", [3]);
        BackupStore.Close();

        Assert.Equal(FormatVersions.Backups, UserVersion(BackupStore.StoreFile));
        Assert.Equal(3L, Scalar(BackupStore.StoreFile, "SELECT COUNT(*) FROM backups WHERE path = '/a.json'"));
    }

    [Fact]
    public void Backups_WithoutVersionAndWithAnUnknownTable_AreUnreadableAndLeftAlone()
    {
        CreateDatabase(BackupStore.StoreFile, userVersion: 0);
        var before = File.ReadAllBytes(BackupStore.StoreFile);

        Assert.Null(Record.Exception(() => BackupStore.Record(PathOf("doc.json"), [1, 2, 3])));

        BackupStore.Close();
        SqliteConnection.ClearAllPools();
        Assert.Equal(before, File.ReadAllBytes(BackupStore.StoreFile));
    }

    [Fact]
    public void Backups_CurrentVersion_RoundTrips()
    {
        BackupStore.Session = "first";
        BackupStore.Record(PathOf("doc.json"), [1]);
        BackupStore.Close();
        Assert.Equal(FormatVersions.Backups, UserVersion(BackupStore.StoreFile));

        BackupStore.Session = "second";
        BackupStore.Record(PathOf("doc.json"), [2]);
        BackupStore.Close();

        Assert.Equal(2L, Scalar(BackupStore.StoreFile, "SELECT COUNT(*) FROM backups"));
    }

    [Fact]
    public void Backups_NewerVersion_IsRefusedAndLeftByteIdentical()
    {
        CreateDatabase(BackupStore.StoreFile, FormatVersions.Backups + 1);
        var before = File.ReadAllBytes(BackupStore.StoreFile);

        Assert.Null(Record.Exception(() => BackupStore.Record(PathOf("doc.json"), [1, 2, 3])));

        BackupStore.Close();
        SqliteConnection.ClearAllPools();
        Assert.Equal(before, File.ReadAllBytes(BackupStore.StoreFile));
    }

    // A refused JSON store is neither quarantined, rebuilt nor reported as a reset.
    private void AssertJsonRefused(string fileName, int current, Func<object> load)
    {
        var file = PathOf(fileName);
        File.WriteAllText(file, $$"""{"formatVersion":{{current + 1}},"future":true}""");
        var before = File.ReadAllBytes(file);

        var refused = Assert.Throws<NewerFormatVersionException>(() => load());

        Assert.Equal(file, refused.FilePath);
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.Empty(Directory.GetFiles(_root, "*.invalid"));
        Assert.Empty(QuarantineJournal.Drain());
    }

    // A disposable store a newer build wrote is neither refused, quarantined nor reported: it reads as
    // defaults, and the file stays as it is until the next save replaces it.
    private void AssertJsonRebuilt(string fileName, int current, Func<object?> loadValue)
    {
        var file = PathOf(fileName);
        File.WriteAllText(file, $$"""{"formatVersion":{{current + 1}},"future":true}""");
        var before = File.ReadAllBytes(file);

        Assert.Null(loadValue());

        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.Empty(Directory.GetFiles(_root, "*.invalid"));
        Assert.Empty(QuarantineJournal.Drain());
    }

    // The marker is written first, so it is the first thing a reader sees.
    private int FirstKeyVersion(string fileName)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(PathOf(fileName)));
        var first = document.RootElement.EnumerateObject().First();
        Assert.Equal("formatVersion", first.Name);
        return first.Value.GetInt32();
    }

    private static void CreateDatabase(string file, int userVersion)
    {
        using var connection = new SqliteConnection($"Data Source={file};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE TABLE earlier (id INTEGER PRIMARY KEY); PRAGMA user_version = {userVersion};";
        command.ExecuteNonQuery();
    }

    private static void Execute(string file, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={file};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static int UserVersion(string file) => Convert.ToInt32(Scalar(file, "PRAGMA user_version"));

    private static object? Scalar(string file, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={file};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
