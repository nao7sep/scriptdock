using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.Storage;
using ScriptDock.Tests.Fakes;
using ScriptDock.ViewModels;
using Xunit;

namespace ScriptDock.Tests.Storage;

[Collection(StorageRootEnvironment.CollectionName)]
public sealed class ConfigStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scriptdock-tests", NanoId.New());
    private readonly string? _previousHome = Environment.GetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable);
    private string ConfigPath => Path.Combine(_root, "config.json");

    public ConfigStoreTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, _root);
    }

    public void Dispose()
    {
        BackupStore.Close();
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, _previousHome);
        Directory.Delete(_root, true);
    }

    [Fact]
    public void MissingFile_UsesLiveBuiltInsWithoutWriting()
    {
        var config = new ConfigStore().Load();
        Assert.False(File.Exists(ConfigPath));
        Assert.Equal([ConfigDefaults.DefaultExtension], config.Extensions);
        Assert.Equal(ConfigDefaults.BuiltInIgnorePatterns, config.IgnorePatterns);
        Assert.Empty(config.RootDirs);
        Assert.Empty(config.Hidden);
    }

    [Fact]
    public async Task Hide_WritesExactlyHiddenAndLeavesOtherSetsAbsent()
    {
        var store = new ConfigStore();
        var config = store.Load();
        var vm = NewViewModel(store, config);
        await vm.ToggleHiddenCommand.ExecuteAsync(new ScriptItem("/scripts/run.command"));
        Assert.Equal(["hidden"], ReadKeys());
        var loaded = store.Load();
        Assert.Equal(["/scripts/run.command"], loaded.Hidden);
        Assert.Equal([ConfigDefaults.DefaultExtension], loaded.Extensions);
        Assert.Equal(ConfigDefaults.BuiltInIgnorePatterns, loaded.IgnorePatterns);
        Assert.Equal("", loaded.UiFontFamily);
        Assert.Equal("system", loaded.Language);
        Assert.Equal(ThemePreference.System, loaded.Theme);
        Assert.Empty(loaded.RootDirs);
    }

    [Fact]
    public async Task Unhide_LastScript_RemovesHiddenAndKeepsTheFile()
    {
        var store = new ConfigStore();
        var vm = NewViewModel(store, store.Load());
        var item = new ScriptItem("/scripts/run.command");
        await vm.ToggleHiddenCommand.ExecuteAsync(item);
        await vm.ToggleHiddenCommand.ExecuteAsync(item);
        Assert.Empty(ReadKeys());
        Assert.Empty(store.Load().Hidden);
    }

    [Fact]
    public async Task Dialog_WritesEverySetThatDiffersAndDropsUnknownKeys()
    {
        // Retired process settings are among the keys a save drops.
        File.WriteAllText(ConfigPath, """{"formatVersion":1,"hidden":["/hidden"],"extensions":[],"version":1,"unknown":true,"killProcessesOnClose":true,"recaptureProcessesOnLaunch":false}""");
        var store = new ConfigStore();
        var config = store.Load();
        var vm = NewViewModel(store, config);
        var draft = new SettingsDialogViewModel(config) { Theme = ThemePreference.Dark };
        Assert.True(await vm.TryApplySettingsAsync(draft));
        Assert.Equal(["extensions", "hidden", "theme"], ReadKeys());
        Assert.Empty(store.Load().Extensions);
        Assert.Equal(["/hidden"], store.Load().Hidden);
    }

    [Fact]
    public async Task Reset_RemovesTheResetSets()
    {
        File.WriteAllText(ConfigPath, """{"formatVersion":1,"extensions":[".sh"],"ignorePatterns":["/custom/"]}""");
        var store = new ConfigStore();
        var config = store.Load();
        var vm = NewViewModel(store, config);
        var draft = new SettingsDialogViewModel(config);
        draft.ResetExtensions();
        draft.ResetIgnorePatterns();
        Assert.True(draft.IsDirty);
        Assert.True(await vm.TryApplySettingsAsync(draft));
        Assert.Empty(ReadKeys());
        Assert.Equal([ConfigDefaults.DefaultExtension], store.Load().Extensions);
        Assert.Equal(ConfigDefaults.BuiltInIgnorePatterns, store.Load().IgnorePatterns);
    }

    [Fact]
    public async Task CopiesEqualToTheirBuiltInAfterCleanup_LoseTheirKeysAtTheNextSave()
    {
        File.WriteAllText(ConfigPath, $$"""{"formatVersion":1,"extensions":[" {{ConfigDefaults.DefaultExtension}}\n"],"uiFontFamily":"  ","theme":"system"}""");
        var store = new ConfigStore();
        var vm = NewViewModel(store, store.Load());
        await vm.ToggleHiddenCommand.ExecuteAsync(new ScriptItem("/scripts/run.command"));
        Assert.Equal(["hidden"], ReadKeys());
    }

    [Fact]
    public async Task InvalidSet_LosesItsKeyAtTheNextSave()
    {
        File.WriteAllText(ConfigPath, """{"formatVersion":1,"extensions":[null,".sh"],"theme":"future","hidden":["/ok"]}""");
        var store = new ConfigStore();
        var config = store.Load();
        await store.SaveAsync(config);
        Assert.Equal(["hidden"], ReadKeys());
    }

    [Fact]
    public void InvalidSet_UsesBuiltInWithoutQuarantiningOrChangingOtherSets()
    {
        const string json = """{"formatVersion":1,"extensions":[null,".sh"],"theme":"future","language":"unknown","hidden":["/ok"]}""";
        File.WriteAllText(ConfigPath, json);
        var store = new ConfigStore();
        var loaded = store.Load();
        Assert.Equal([ConfigDefaults.DefaultExtension], loaded.Extensions);
        Assert.Equal(ThemePreference.System, loaded.Theme);
        Assert.Equal("system", loaded.Language);
        Assert.Equal(["/ok"], loaded.Hidden);
        Assert.Equal(json, File.ReadAllText(ConfigPath));
        Assert.Empty(Directory.GetFiles(_root, "*.invalid"));
    }

    [Theory]
    [InlineData("""["["]""")]                 // does not compile
    [InlineData("""["/build/",""]""")]        // empty: would match every path
    [InlineData("""["/build/","  "]""")]      // blank
    [InlineData("""["/build/\n/dist/"]""")]  // multiline, which cleanup would flatten into one rule
    public void InvalidIgnorePatterns_ReadAsTheirBuiltIn_AndKeepTheOtherSets(string patterns)
    {
        File.WriteAllText(ConfigPath, $$"""{"formatVersion":1,"ignorePatterns":{{patterns}},"extensions":[".sh"],"hidden":["/ok"]}""");
        var loaded = new ConfigStore().Load();
        Assert.Equal(ConfigDefaults.BuiltInIgnorePatterns, loaded.IgnorePatterns);
        Assert.Equal([".sh"], loaded.Extensions);
        Assert.Equal(["/ok"], loaded.Hidden);
    }

    [Theory]
    [InlineData("""[".sh",""]""")]            // empty
    [InlineData("""[".s h"]""")]              // interior space
    [InlineData("""[".sh\n.py"]""")]         // multiline, which cleanup would flatten into ".sh .py"
    public void InvalidExtensions_ReadAsTheirBuiltIn_AndKeepTheOtherSets(string extensions)
    {
        File.WriteAllText(ConfigPath, $$"""{"formatVersion":1,"extensions":{{extensions}},"ignorePatterns":["/build/"],"hidden":["/ok"]}""");
        var loaded = new ConfigStore().Load();
        Assert.Equal([ConfigDefaults.DefaultExtension], loaded.Extensions);
        Assert.Equal(["/build/"], loaded.IgnorePatterns);
        Assert.Equal(["/ok"], loaded.Hidden);
    }

    [Fact]
    public async Task InvalidIgnorePatterns_LoseTheirKeyAtTheNextSave()
    {
        File.WriteAllText(ConfigPath, """{"formatVersion":1,"ignorePatterns":["["],"hidden":["/ok"]}""");
        var store = new ConfigStore();
        await store.SaveAsync(store.Load());
        Assert.Equal(["hidden"], ReadKeys());
    }

    [Fact]
    public void BuiltInTexts_AreKeptCleaned()
    {
        Assert.Equal(TextCleanup.SingleLine(ConfigDefaults.DefaultExtension), ConfigDefaults.DefaultExtension);
        Assert.All(ConfigDefaults.BuiltInIgnorePatterns, pattern => Assert.Equal(TextCleanup.SingleLine(pattern), pattern));
    }

    [Fact]
    public void CorruptFile_IsQuarantinedWithoutSeedingAReplacement()
    {
        File.WriteAllText(ConfigPath, "{ broken");
        var loaded = new ConfigStore().Load();
        Assert.False(File.Exists(ConfigPath));
        Assert.Single(Directory.GetFiles(_root, "*.invalid"));
        Assert.Equal([ConfigDefaults.DefaultExtension], loaded.Extensions);
    }

    [Fact]
    public async Task OverlappingWrites_EachSnapshotTheirCallAndLandInOrder()
    {
        var store = new ConfigStore();
        var config = store.Load();
        config.Hidden.Add("/one");
        var first = store.SaveAsync(config);
        config.Hidden.Add("/two");
        var second = store.SaveAsync(config);
        config.Extensions = [".sh"];
        var third = store.SaveAsync(config);
        config.Extensions.Add(".late");
        await Task.WhenAll(first, second, third);
        Assert.Equal(["/one", "/two"], store.Load().Hidden);
        Assert.Equal([".sh"], store.Load().Extensions);
        Assert.Equal(["extensions", "hidden"], ReadKeys());
    }

    private static MainWindowViewModel NewViewModel(ConfigStore store, AppConfig config) =>
        new(store, new FakeJsonStore<AppState>(), new FakeJsonStore<KnownPaths>(), new FakeRecordStore(), config, new AppState(), new KnownPaths(), new ScriptScanner(), new FakeProcessRunner());

    // The sets the file holds; the format version beside them is the store's own.
    private string[] ReadKeys()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(ConfigPath));
        return document.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .Where(name => name != "formatVersion")
            .Order()
            .ToArray();
    }
}
