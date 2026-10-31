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
/// Each store's identity header (file-identity-conventions): the current version round-trips with the header
/// first, a store without the header or with another owner or kind is unreadable, and a newer one is refused
/// with the file left byte-identical, except in the disposable stores, which rebuild instead.
/// </summary>
[Collection(StorageRootEnvironment.CollectionName)]
public sealed class FileHeaderTests : IDisposable
{
    private static readonly DateTimeOffset SessionStart = new(2026, 10, 5, 1, 2, 3, 456, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "scriptdock-tests", NanoId.New());
    private readonly string? _previousHome = Environment.GetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable);

    public FileHeaderTests()
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

    [Fact]
    public void Config_WithoutHeader_IsQuarantined()
    {
        File.WriteAllText(PathOf(AppPaths.ConfigFileName), """{"theme":"dark","rootDirs":["/scripts"]}""");

        var config = new ConfigStore().Load();

        Assert.Equal(ThemePreference.System, config.Theme);
        Assert.Empty(config.RootDirs);
        Assert.Single(Directory.GetFiles(_root, "config-*.invalid"));
    }

    [Theory]
    [InlineData("pathhide", FileHeader.Kinds.Config)]
    [InlineData(FileHeader.Owner, FileHeader.Kinds.State)]
    public void Config_OfAnotherOwnerOrKind_IsQuarantined(string owner, string kind)
    {
        File.WriteAllText(PathOf(AppPaths.ConfigFileName),
            $$"""{"format":{"owner":"{{owner}}","kind":"{{kind}}","version":1},"theme":"dark"}""");

        Assert.Equal(ThemePreference.System, new ConfigStore().Load().Theme);
        Assert.Single(Directory.GetFiles(_root, "config-*.invalid"));
    }

    [Fact]
    public void Config_IsReadWhateverTheWriterSays()
    {
        File.WriteAllText(PathOf(AppPaths.ConfigFileName),
            """{"format":{"owner":"scriptdock","kind":"config","version":1},"writer":{"name":"SomethingElse","version":"0.0.1"},"theme":"dark"}""");

        Assert.Equal(ThemePreference.Dark, new ConfigStore().Load().Theme);
        Assert.Empty(Directory.GetFiles(_root, "*.invalid"));
    }

    [Fact]
    public async Task Config_CurrentVersion_RoundTrips()
    {
        var store = new ConfigStore();
        var config = store.Load();
        config.RootDirs = ["/scripts"];
        await store.SaveAsync(config);

        AssertHeaderFirst(AppPaths.ConfigFileName, FileHeader.Kinds.Config);
        Assert.Equal(["/scripts"], store.Load().RootDirs);
    }

    [Fact]
    public void Config_NewerVersion_IsRefusedAndLeftByteIdentical() =>
        AssertJsonRefused(AppPaths.ConfigFileName, FileHeader.Kinds.Config, () => new ConfigStore().Load());

    // hidden.json

    [Fact]
    public void Hidden_WithoutHeader_IsQuarantined()
    {
        File.WriteAllText(PathOf(AppPaths.HiddenFileName), """{"paths":["/a.command"]}""");

        Assert.Empty(AppStores.Hidden().Load().Paths);
        Assert.Single(Directory.GetFiles(_root, "hidden-*.invalid"));
    }

    [Fact]
    public async Task Hidden_CurrentVersion_RoundTrips()
    {
        var store = AppStores.Hidden();
        await store.SaveAsync(new HiddenScripts { Paths = ["/a.command"] });

        AssertHeaderFirst(AppPaths.HiddenFileName, FileHeader.Kinds.HiddenScripts);
        Assert.Equal(["/a.command"], store.Load().Paths);
    }

    [Fact]
    public void Hidden_NewerVersion_IsRefusedAndLeftByteIdentical() =>
        AssertJsonRefused(AppPaths.HiddenFileName, FileHeader.Kinds.HiddenScripts, () => AppStores.Hidden().Load());

    // The user's hides are not rebuilt from nothing: a member that is not a path quarantines the file.
    [Fact]
    public void Hidden_WithAMemberThatIsNotAPath_IsQuarantined()
    {
        File.WriteAllText(PathOf(AppPaths.HiddenFileName), """{"format":{"owner":"scriptdock","kind":"hiddenScripts","version":1},"paths":["/a.command",null]}""");

        Assert.Empty(AppStores.Hidden().Load().Paths);
        var quarantined = Assert.Single(Directory.GetFiles(_root, "hidden-*.invalid"));
        Assert.Equal([quarantined], QuarantineJournal.Drain());
    }

    // state.json

    // Disposable view state: a file without the header reads as defaults and is replaced on the next save.
    [Fact]
    public void State_WithoutHeader_IsRebuilt()
    {
        File.WriteAllText(PathOf(AppPaths.StateFileName), """{"windowWidth":900}""");

        Assert.Null(AppStores.State().Load().WindowWidth);
        Assert.Empty(Directory.GetFiles(_root, "*.invalid"));
    }

    [Fact]
    public async Task State_CurrentVersion_RoundTrips()
    {
        var store = AppStores.State();
        await store.SaveAsync(new AppState { WindowWidth = 900 });

        AssertHeaderFirst(AppPaths.StateFileName, FileHeader.Kinds.State);
        Assert.Equal(900, store.Load().WindowWidth);
    }

    [Fact]
    public async Task State_NewerVersion_IsRebuilt()
    {
        AssertJsonRebuilt(AppPaths.StateFileName, FileHeader.Kinds.State, () => AppStores.State().Load().WindowWidth);
        await AppStores.State().SaveAsync(new AppState { WindowWidth = 900 });
        AssertHeaderFirst(AppPaths.StateFileName, FileHeader.Kinds.State);
    }

    // known-paths.json

    [Fact]
    public void KnownPaths_WithoutHeader_AreNoBaseline()
    {
        File.WriteAllText(PathOf(AppPaths.KnownPathsFileName), """{"paths":["/a.command"]}""");

        Assert.Null(AppStores.KnownPaths().Load().Paths);
        Assert.Empty(Directory.GetFiles(_root, "*.invalid"));
    }

    [Theory]
    [InlineData("""["/a.command",null]""")]
    [InlineData("""["/a.command",1]""")]
    public void KnownPaths_WithAMemberThatIsNotAPath_AreNoBaseline(string paths)
    {
        File.WriteAllText(PathOf(AppPaths.KnownPathsFileName), $$"""{"format":{"owner":"scriptdock","kind":"knownPaths","version":1},"paths":{{paths}}}""");

        Assert.Null(AppStores.KnownPaths().Load().Paths);
        Assert.Empty(Directory.GetFiles(_root, "*.invalid"));
    }

    [Fact]
    public void KnownPaths_WithAnEmptyList_AreABaseline()
    {
        File.WriteAllText(PathOf(AppPaths.KnownPathsFileName), """{"format":{"owner":"scriptdock","kind":"knownPaths","version":1},"paths":[]}""");

        Assert.Equal([], AppStores.KnownPaths().Load().Paths!);
    }

    [Fact]
    public async Task KnownPaths_CurrentVersion_RoundTrips()
    {
        var store = AppStores.KnownPaths();
        await store.SaveAsync(new KnownPaths { Paths = ["/a.command"] });

        AssertHeaderFirst(AppPaths.KnownPathsFileName, FileHeader.Kinds.KnownPaths);
        Assert.Equal(["/a.command"], store.Load().Paths);
    }

    [Fact]
    public void KnownPaths_NewerVersion_IsRebuilt() =>
        AssertJsonRebuilt(AppPaths.KnownPathsFileName, FileHeader.Kinds.KnownPaths, () => AppStores.KnownPaths().Load().Paths);

    // records.sqlite3

    // A database from before the header: ScriptDock's own tables at user_version 1, with no file_header.
    private const string HeaderlessRecords = """
        CREATE TABLE runs (id INTEGER PRIMARY KEY, session TEXT NOT NULL, run INTEGER NOT NULL, time TEXT NOT NULL, script TEXT NOT NULL, pid INTEGER, os_started_at TEXT, output_path TEXT, UNIQUE (session, run));
        INSERT INTO runs (session, run, time, script) VALUES ('2026-10-03T00:00:00.000Z', 1, '2026-10-03T00:00:00.000Z', '/old.command');
        PRAGMA user_version = 1;
        """;

    [Fact]
    public void Records_New_GetsTheHeaderOnCreation()
    {
        using (var records = new RecordStore(_root, SessionStart))
            Assert.False(records.DatabaseUnavailable);

        var file = PathOf(RecordStore.FileName);
        Assert.Equal(FileHeader.Version, UserVersion(file));
        AssertDatabaseHeader(file, FileHeader.Kinds.Records);
    }

    [Fact]
    public void Records_WithoutHeader_AreUnavailableAndLeftByteIdentical()
    {
        var file = PathOf(RecordStore.FileName);
        Execute(file, HeaderlessRecords);
        AssertRecordsUnavailable(file);
    }

    [Theory]
    [InlineData("pathhide", FileHeader.Kinds.Records)]
    [InlineData(FileHeader.Owner, FileHeader.Kinds.Backups)]
    public void Records_OfAnotherOwnerOrKind_AreUnavailableAndLeftByteIdentical(string owner, string kind)
    {
        var file = PathOf(RecordStore.FileName);
        CreateDatabase(file, 1, owner, kind);
        AssertRecordsUnavailable(file);
    }

    [Fact]
    public void Records_AtVersionZeroWithAHeader_AreUnavailable()
    {
        var file = PathOf(RecordStore.FileName);
        CreateDatabase(file, 0, FileHeader.Owner, FileHeader.Kinds.Records);
        AssertRecordsUnavailable(file);
    }

    // The writer rows are never read, only rewritten when the app opens the database for writing.
    [Fact]
    public void Records_IgnoreTheWriterAndRewriteIt()
    {
        var file = PathOf(RecordStore.FileName);
        CreateDatabase(file, 1, FileHeader.Owner, FileHeader.Kinds.Records);
        Execute(file, """
            INSERT INTO file_header (key, value) VALUES ('writer.name', 'SomethingElse'), ('writer.version', '9.9.9'),
                ('writer.caller.name', 'x'), ('writer.caller.version', 'y');
            """);

        using (var records = new RecordStore(_root, SessionStart))
            Assert.False(records.DatabaseUnavailable);

        AssertDatabaseHeader(file, FileHeader.Kinds.Records);
    }

    [Fact]
    public async Task Records_CurrentVersion_RoundTrips()
    {
        using (var records = new RecordStore(_root, SessionStart))
            await records.AddRunAsync(new RunRecord("s", 1, SessionStart, "/a.command", null, null, null));

        Assert.Equal(FileHeader.Version, UserVersion(PathOf(RecordStore.FileName)));
        AssertDatabaseHeader(PathOf(RecordStore.FileName), FileHeader.Kinds.Records);
        using var reopened = new RecordStore(_root, SessionStart.AddMinutes(1));
        Assert.Equal("/a.command", Assert.Single(await reopened.ReadRecentAsync()).Path);
    }

    [Fact]
    public void Records_NewerVersion_IsRefusedAndLeftByteIdentical()
    {
        var file = PathOf(RecordStore.FileName);
        CreateDatabase(file, FileHeader.Version + 1, FileHeader.Owner, FileHeader.Kinds.Records);
        var before = File.ReadAllBytes(file);

        var refused = Assert.Throws<NewerFormatVersionException>(() => new RecordStore(_root, SessionStart));

        Assert.Equal(file, refused.FilePath);
        SqliteConnection.ClearAllPools();
        Assert.Equal(before, File.ReadAllBytes(file));
    }

    // backups.sqlite3, a side store: a newer one disables recording for the session instead of stopping the app.

    [Fact]
    public void Backups_CurrentVersion_RoundTrips()
    {
        BackupStore.Session = "first";
        BackupStore.Record(PathOf("doc.json"), [1]);
        BackupStore.Close();
        Assert.Equal(FileHeader.Version, UserVersion(BackupStore.StoreFile));
        AssertDatabaseHeader(BackupStore.StoreFile, FileHeader.Kinds.Backups);

        BackupStore.Session = "second";
        BackupStore.Record(PathOf("doc.json"), [2]);
        BackupStore.Close();

        Assert.Equal(2L, Scalar(BackupStore.StoreFile, "SELECT COUNT(*) FROM backups"));
    }

    [Fact]
    public void Backups_NewerVersion_IsRefusedAndLeftByteIdentical()
    {
        CreateDatabase(BackupStore.StoreFile, FileHeader.Version + 1, FileHeader.Owner, FileHeader.Kinds.Backups);
        var before = File.ReadAllBytes(BackupStore.StoreFile);

        Assert.Null(Record.Exception(() => BackupStore.Record(PathOf("doc.json"), [1, 2, 3])));

        BackupStore.Close();
        SqliteConnection.ClearAllPools();
        Assert.Equal(before, File.ReadAllBytes(BackupStore.StoreFile));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("pathhide", FileHeader.Kinds.Backups)]
    [InlineData(FileHeader.Owner, FileHeader.Kinds.Records)]
    public void Backups_WithoutHeaderOrOfAnotherFormat_AreOffAndLeftByteIdentical(string? owner, string? kind)
    {
        if (owner is null)
            Execute(BackupStore.StoreFile, "CREATE TABLE backups (id INTEGER PRIMARY KEY); PRAGMA user_version = 1;");
        else
            CreateDatabase(BackupStore.StoreFile, 1, owner, kind!);
        var before = File.ReadAllBytes(BackupStore.StoreFile);

        Assert.Null(Record.Exception(() => BackupStore.Record(PathOf("doc.json"), [1, 2, 3])));

        BackupStore.Close();
        SqliteConnection.ClearAllPools();
        Assert.Equal(before, File.ReadAllBytes(BackupStore.StoreFile));
    }

    private void AssertRecordsUnavailable(string file)
    {
        var before = File.ReadAllBytes(file);

        using (var records = new RecordStore(_root, SessionStart))
            Assert.True(records.DatabaseUnavailable);

        SqliteConnection.ClearAllPools();
        Assert.Equal(before, File.ReadAllBytes(file));
    }

    private static void AssertDatabaseHeader(string file, string kind)
    {
        Assert.Equal(FileHeader.Owner, Scalar(file, "SELECT value FROM file_header WHERE key = 'format.owner'"));
        Assert.Equal(kind, Scalar(file, "SELECT value FROM file_header WHERE key = 'format.kind'"));
        Assert.Equal(FileHeader.WriterName, Scalar(file, "SELECT value FROM file_header WHERE key = 'writer.name'"));
        Assert.Equal(FileHeader.WriterVersion, Scalar(file, "SELECT value FROM file_header WHERE key = 'writer.version'"));
        Assert.Equal(4L, Scalar(file, "SELECT COUNT(*) FROM file_header"));
    }

    // A refused JSON store is neither quarantined, rebuilt nor reported as a reset.
    private void AssertJsonRefused(string fileName, string kind, Func<object> load)
    {
        var file = PathOf(fileName);
        File.WriteAllText(file, NewerJson(kind));
        var before = File.ReadAllBytes(file);

        var refused = Assert.Throws<NewerFormatVersionException>(() => load());

        Assert.Equal(file, refused.FilePath);
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.Empty(Directory.GetFiles(_root, "*.invalid"));
        Assert.Empty(QuarantineJournal.Drain());
    }

    // A disposable store a newer build wrote is neither refused, quarantined nor reported: it reads as
    // defaults, and the file stays as it is until the next save replaces it.
    private void AssertJsonRebuilt(string fileName, string kind, Func<object?> loadValue)
    {
        var file = PathOf(fileName);
        File.WriteAllText(file, NewerJson(kind));
        var before = File.ReadAllBytes(file);

        Assert.Null(loadValue());

        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.Empty(Directory.GetFiles(_root, "*.invalid"));
        Assert.Empty(QuarantineJournal.Drain());
    }

    private static string NewerJson(string kind) =>
        $$"""{"format":{"owner":"scriptdock","kind":"{{kind}}","version":{{FileHeader.Version + 1}}},"future":true}""";

    // The header is written first, so it is the first thing a reader sees.
    private void AssertHeaderFirst(string fileName, string kind)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(PathOf(fileName)));
        Assert.Equal(["format", "writer"], document.RootElement.EnumerateObject().Select(p => p.Name).Take(2));
        var format = document.RootElement.GetProperty("format");
        Assert.Equal(FileHeader.Owner, format.GetProperty("owner").GetString());
        Assert.Equal(kind, format.GetProperty("kind").GetString());
        Assert.Equal(FileHeader.Version, format.GetProperty("version").GetInt32());
    }

    private static void CreateDatabase(string file, int userVersion, string owner, string kind) =>
        Execute(file, $"""
            CREATE TABLE file_header (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO file_header (key, value) VALUES ('format.owner', '{owner}'), ('format.kind', '{kind}');
            CREATE TABLE earlier (id INTEGER PRIMARY KEY);
            PRAGMA user_version = {userVersion};
            """);

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
