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
/// Each store's format version (store-recovery-conventions): a missing marker reads as 1, the current
/// version round-trips, and a newer one is refused with the file left byte-identical.
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

    [Fact]
    public void Config_MissingMarker_ReadsAsOne()
    {
        File.WriteAllText(PathOf(AppPaths.ConfigFileName), """{"hidden":["/a.command"]}""");

        Assert.Equal(["/a.command"], new ConfigStore().Load().Hidden);
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
    public void State_MissingMarker_ReadsAsOne()
    {
        File.WriteAllText(PathOf(AppPaths.StateFileName), """{"windowWidth":900}""");

        Assert.Equal(900, AppStores.State().Load().WindowWidth);
    }

    [Fact]
    public void State_CurrentVersion_RoundTrips()
    {
        var store = AppStores.State();
        store.Save(new AppState { WindowWidth = 900 });

        Assert.Equal(FormatVersions.State, FirstKeyVersion(AppPaths.StateFileName));
        Assert.Equal(900, store.Load().WindowWidth);
    }

    [Fact]
    public void State_NewerVersion_IsRefusedAndLeftByteIdentical() =>
        AssertJsonRefused(AppPaths.StateFileName, FormatVersions.State, () => AppStores.State().Load());

    // known-paths.json

    [Fact]
    public void KnownPaths_MissingMarker_ReadsAsOne()
    {
        File.WriteAllText(PathOf(AppPaths.KnownPathsFileName), """{"paths":["/a.command"]}""");

        Assert.Equal(["/a.command"], AppStores.KnownPaths().Load().Paths);
    }

    [Fact]
    public void KnownPaths_CurrentVersion_RoundTrips()
    {
        var store = AppStores.KnownPaths();
        store.Save(new KnownPaths { Paths = ["/a.command"] });

        Assert.Equal(FormatVersions.KnownPaths, FirstKeyVersion(AppPaths.KnownPathsFileName));
        Assert.Equal(["/a.command"], store.Load().Paths);
    }

    [Fact]
    public void KnownPaths_NewerVersion_IsRefusedAndLeftByteIdentical() =>
        AssertJsonRefused(AppPaths.KnownPathsFileName, FormatVersions.KnownPaths, () => AppStores.KnownPaths().Load());

    // records.sqlite3

    [Fact]
    public async Task Records_MissingMarker_ReadsAsOne()
    {
        var file = PathOf(RecordStore.FileName);
        CreateDatabase(file, userVersion: 0);

        using (var records = new RecordStore(_root, SessionStart))
            await records.AddDismissalAsync("/a.command");

        Assert.Equal(FormatVersions.Records, UserVersion(file));
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
    public void Backups_MissingMarker_ReadsAsOne()
    {
        CreateDatabase(BackupStore.StoreFile, userVersion: 0);

        BackupStore.Record(PathOf("doc.json"), [1, 2, 3]);
        BackupStore.Close();

        Assert.Equal(1L, Scalar(BackupStore.StoreFile, "SELECT COUNT(*) FROM backups"));
        Assert.Equal(FormatVersions.Backups, UserVersion(BackupStore.StoreFile));
    }

    [Fact]
    public void Backups_CurrentVersion_RoundTrips()
    {
        BackupStore.Record(PathOf("doc.json"), [1]);
        BackupStore.Close();
        Assert.Equal(FormatVersions.Backups, UserVersion(BackupStore.StoreFile));

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
