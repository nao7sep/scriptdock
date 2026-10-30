using System;
using System.Collections.Generic;
using System.Reflection;
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
public sealed class ConfigStoreTests : IAsyncLifetime
{
    private readonly List<MainWindowViewModel> _vms = [];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scriptdock-tests", NanoId.New());
    private readonly string? _previousHome = Environment.GetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable);
    private string ConfigPath => Path.Combine(_root, "config.json");

    public ConfigStoreTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, _root);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        try { await Task.WhenAll(_vms.Select(vm => vm.ShutdownAsync())); }
        finally
        {
            try { BackupStore.Close(); }
            finally
            {
                Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, _previousHome);
                Directory.Delete(_root, true);
            }
        }
    }

    [Fact]
    public void MissingFile_UsesLiveBuiltInsWithoutWriting()
    {
        var config = new ConfigStore().Load();
        Assert.False(File.Exists(ConfigPath));
        Assert.Equal([ConfigDefaults.DefaultExtension], config.Extensions);
        Assert.Equal(ConfigDefaults.BuiltInIgnorePatterns, config.IgnorePatterns);
        Assert.Empty(config.RootDirs);
    }

    [Fact]
    public async Task Hide_WritesTheHiddenListAndLeavesTheSettingsUnwritten()
    {
        var store = new ConfigStore();
        var config = store.Load();
        var vm = NewViewModel(store, config);
        await vm.ToggleHiddenCommand.ExecuteAsync(new ScriptItem("/scripts/run.command"));
        Assert.False(File.Exists(ConfigPath));
        Assert.Equal(["/scripts/run.command"], AppStores.Hidden().Load().Paths);
    }

    [Fact]
    public async Task Unhide_LastScript_RemovesHiddenAndKeepsTheFile()
    {
        var store = new ConfigStore();
        var vm = NewViewModel(store, store.Load());
        var item = new ScriptItem("/scripts/run.command");
        await vm.ToggleHiddenCommand.ExecuteAsync(item);
        await vm.ToggleHiddenCommand.ExecuteAsync(item);
        Assert.True(File.Exists(Path.Combine(_root, "hidden.json")));
        Assert.Empty(AppStores.Hidden().Load().Paths);
    }

    [Fact]
    public async Task Dialog_WritesEverySetThatDiffers_AndKeepsUnknownKeys()
    {
        // Keys this version does not know are kept unchanged.
        File.WriteAllText(ConfigPath, """{"formatVersion":1,"hidden":["/hidden"],"extensions":[],"version":1,"unknown":true}""");
        var store = new ConfigStore();
        var config = store.Load();
        var vm = NewViewModel(store, config);
        var draft = new SettingsDialogViewModel(config) { Theme = ThemePreference.Dark };
        Assert.True(await vm.TryApplySettingsAsync(draft));
        Assert.Equal(["extensions", "hidden", "theme", "unknown", "version"], ReadKeys());
        Assert.Equal("true", ReadRaw("unknown"));
        // An empty extension list would find nothing, so it is kept unchanged and the built-in applies.
        Assert.Equal("[]", ReadRaw("extensions"));
        Assert.Equal([ConfigDefaults.DefaultExtension], store.Load().Extensions);
        Assert.Contains("extensions", store.KeptKeys);
        // A hidden list from before hidden.json stays until the startup move takes it, and is not a kept setting.
        store.Load();
        Assert.Equal(["/hidden"], store.MovedHidden);
        Assert.DoesNotContain("hidden", store.KeptKeys);
    }

    // The startup notice says to fix a kept set in Settings: choosing its built-in there is a real change.
    [Fact]
    public async Task Resetting_a_kept_set_in_Settings_replaces_it_with_the_built_in()
    {
        File.WriteAllText(ConfigPath, """{"formatVersion":1,"extensions":[],"ignorePatterns":["["]}""");
        var store = new ConfigStore();
        var vm = NewViewModel(store, store.Load());
        var draft = vm.CreateSettingsDraft();
        Assert.False(draft.IsDirty);

        draft.ResetExtensions();
        Assert.True(draft.IsDirty);
        Assert.True(await vm.TryApplySettingsAsync(draft));

        Assert.Equal(["ignorePatterns"], ReadKeys()); // the set not reset stays as it was
        Assert.Equal("""["["]""", ReadRaw("ignorePatterns"));
        Assert.Equal([ConfigDefaults.DefaultExtension], store.Load().Extensions);
    }

    // Order has no meaning, and the scanner ignores case: reordering the built-ins or re-adding one in
    // another case is still the built-in, so nothing is written.
    [Fact]
    public async Task Lists_are_compared_as_sets_so_a_reordered_built_in_leaves_no_copy()
    {
        var store = new ConfigStore();
        await store.SaveAsync(new AppConfig
        {
            Extensions = [ConfigDefaults.DefaultExtension.ToUpperInvariant()],
            IgnorePatterns = [.. ConfigDefaults.BuiltInIgnorePatterns.Reverse()],
        });
        Assert.False(File.Exists(ConfigPath) && ReadKeys().Length > 0);
        Assert.True(ConfigSets.Same(new AppConfig { IgnorePatterns = [.. ConfigDefaults.BuiltInIgnorePatterns.Reverse()] }, new AppConfig()));
    }

    [Fact]
    public async Task Reset_RemovesTheResetSets()
    {
        File.WriteAllText(ConfigPath, """{"formatVersion":1,"extensions":[".sh"],"ignorePatterns":[{"pattern":"/custom/","kind":"regex"}]}""");
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
        await store.SaveAsync(store.Load());
        Assert.Empty(ReadKeys());
    }

    [Fact]
    public async Task InvalidSet_IsKeptUnchangedAtTheNextSave()
    {
        File.WriteAllText(ConfigPath, """{"formatVersion":1,"extensions":[null,".sh"],"theme":"future","hidden":["/ok"]}""");
        var store = new ConfigStore();
        var config = store.Load();
        Assert.Equal(["extensions", "theme"], store.KeptKeys.Order());
        config.RootDirs.Add("/new");
        await store.SaveAsync(config);
        Assert.Equal(["extensions", "hidden", "rootDirs", "theme"], ReadKeys());
        Assert.Equal("""[null,".sh"]""", ReadRaw("extensions"));
        Assert.Equal("\"future\"", ReadRaw("theme"));
    }

    [Fact]
    public async Task InvalidSet_IsReplacedOnceTheUserSavesANewValue()
    {
        File.WriteAllText(ConfigPath, """{"formatVersion":1,"extensions":[null,".sh"],"hidden":["/ok"]}""");
        var store = new ConfigStore();
        var config = store.Load();
        config.Extensions = [".py"];
        await store.SaveAsync(config);
        Assert.Equal("""[".py"]""", ReadRaw("extensions"));

        // Replaced for good: a later save that returns the set to its built-in removes it.
        config.Extensions = [ConfigDefaults.DefaultExtension];
        await store.SaveAsync(config);
        Assert.Equal(["hidden"], ReadKeys());
    }

    [Fact]
    public async Task FailedReplacement_KeepsOriginalSetForALaterUnrelatedSave()
    {
        const string original = """{"formatVersion":1,"extensions":[null,".sh"]}""";
        File.WriteAllText(ConfigPath, original);
        var store = new ConfigStore();
        var config = store.Load();
        var vm = NewViewModel(store, config);
        var draft = new SettingsDialogViewModel(config);
        draft.ExtensionsText = ".py";
        // A directory at the destination forces a real publication failure on both platforms.
        File.Move(ConfigPath, ConfigPath + ".held");
        Directory.CreateDirectory(ConfigPath);
        try
        {
            Assert.False(await vm.TryApplySettingsAsync(draft));
            Assert.Equal(["extensions"], store.KeptKeys);
        }
        finally
        {
            Directory.Delete(ConfigPath);
            File.Move(ConfigPath + ".held", ConfigPath);
        }
        config.RootDirs = ["/later"];
        await store.SaveAsync(config);
        Assert.Equal("""[null,".sh"]""", ReadRaw("extensions"));
        Assert.Equal(["extensions", "rootDirs"], ReadKeys());
        await vm.ShutdownAsync();
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
        Assert.Equal(["/ok"], store.MovedHidden);
        Assert.Equal(json, File.ReadAllText(ConfigPath));
        Assert.Empty(Directory.GetFiles(_root, "*.invalid"));
    }

    [Theory]
    [InlineData("""["["]""")]                 // does not compile
    [InlineData("""["/build/",""]""")]        // empty: would match every path
    [InlineData("""["/build/","  "]""")]      // blank
    [InlineData("""["/build/\n/dist/"]""")]  // multiline, which cleanup would flatten into one rule
    [InlineData("""[{"pattern":"[","kind":"regex"}]""")]       // a regular expression that does not compile
    [InlineData("""[{"pattern":"//"}]""")]                     // a wildcard that names nothing
    [InlineData("""[{"pattern":"build/","kind":"glob"}]""")]   // a kind this version does not know
    [InlineData("""[{"kind":"wildcard"}]""")]                  // no pattern
    [InlineData("""[3]""")]                                    // not a pattern at all
    public void InvalidIgnorePatterns_ReadAsTheirBuiltIn_AndKeepTheOtherSets(string patterns)
    {
        File.WriteAllText(ConfigPath, $$"""{"formatVersion":1,"ignorePatterns":{{patterns}},"extensions":[".sh"],"hidden":["/ok"]}""");
        var store = new ConfigStore();
        var loaded = store.Load();
        Assert.Equal(ConfigDefaults.BuiltInIgnorePatterns, loaded.IgnorePatterns);
        Assert.Equal([".sh"], loaded.Extensions);
        Assert.Equal(["/ok"], store.MovedHidden);
    }

    [Theory]
    [InlineData("""[".sh",""]""")]            // empty
    [InlineData("""[".s h"]""")]              // interior space
    [InlineData("""[".sh\n.py"]""")]         // multiline, which cleanup would flatten into ".sh .py"
    public void InvalidExtensions_ReadAsTheirBuiltIn_AndKeepTheOtherSets(string extensions)
    {
        File.WriteAllText(ConfigPath, $$"""{"formatVersion":1,"extensions":{{extensions}},"ignorePatterns":[{"pattern":"/build/","kind":"regex"}],"hidden":["/ok"]}""");
        var store = new ConfigStore();
        var loaded = store.Load();
        Assert.Equal([ConfigDefaults.DefaultExtension], loaded.Extensions);
        Assert.Equal([new IgnorePattern("/build/", IgnorePatternKind.Regex)], loaded.IgnorePatterns);
        Assert.Equal(["/ok"], store.MovedHidden);
    }

    // A pattern is stored with its kind, and a built-in with its id.
    [Fact]
    public async Task IgnorePatterns_RoundTripWithTheirKindAndId()
    {
        var store = new ConfigStore();
        var patterns = new List<IgnorePattern>
        {
            new(".git/", Id: "dev.git"),
            new("*.bak.command"),
            new("/old/", IgnorePatternKind.Regex),
        };
        await store.SaveAsync(new AppConfig { IgnorePatterns = patterns });

        Assert.Equal(
            """[{"pattern":".git/","kind":"wildcard","id":"dev.git"},{"pattern":"*.bak.command","kind":"wildcard"},{"pattern":"/old/","kind":"regex"}]""",
            ReadRaw("ignorePatterns"));
        Assert.Equal(patterns, new ConfigStore().Load().IgnorePatterns);
    }

    [Fact]
    public async Task InvalidIgnorePatterns_AreKeptUnchangedAtTheNextSave()
    {
        File.WriteAllText(ConfigPath, """{"formatVersion":1,"ignorePatterns":["["],"hidden":["/ok"]}""");
        var store = new ConfigStore();
        await store.SaveAsync(store.Load());
        Assert.Equal(["hidden", "ignorePatterns"], ReadKeys());
        Assert.Equal("""["["]""", ReadRaw("ignorePatterns"));
    }

    [Fact]
    public void BuiltInTexts_AreKeptCleaned()
    {
        Assert.Equal(TextCleanup.SingleLine(ConfigDefaults.DefaultExtension), ConfigDefaults.DefaultExtension);
        Assert.All(ConfigDefaults.BuiltInIgnorePatterns, pattern => Assert.Equal(TextCleanup.SingleLine(pattern.Pattern), pattern.Pattern));
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
        config.RootDirs.Add("/one");
        var first = store.SaveAsync(config);
        config.RootDirs.Add("/two");
        var second = store.SaveAsync(config);
        config.Extensions = [".sh"];
        var third = store.SaveAsync(config);
        config.Extensions.Add(".late");
        await Task.WhenAll(first, second, third);
        Assert.Equal(["/one", "/two"], store.Load().RootDirs);
        Assert.Equal([".sh"], store.Load().Extensions);
        Assert.Equal(["extensions", "rootDirs"], ReadKeys());
    }

    // A "hidden" entry that is not a list of paths is not a hidden list to move: it stays as an unknown key.
    [Fact]
    public async Task A_hidden_entry_that_is_not_a_list_of_paths_is_kept_as_an_unknown_key()
    {
        File.WriteAllText(ConfigPath, """{"formatVersion":1,"hidden":[3]}""");
        var store = new ConfigStore();
        var config = store.Load();
        Assert.Null(store.MovedHidden);
        Assert.Equal(["hidden"], store.KeptKeys);
        await store.SaveAsync(config);
        Assert.Equal("[3]", ReadRaw("hidden"));
    }

    // The startup move: the list leaves config.json for hidden.json, the other sets stay, and a second
    // launch has nothing left to move.
    [Fact]
    public void Startup_moves_a_hidden_list_out_of_config_json_once()
    {
        File.WriteAllText(ConfigPath, """{"formatVersion":1,"hidden":["/a.command","/b.command"],"theme":"dark","unknown":true}""");
        var first = new StartupStores(new ConfigStore(), AppStores.Hidden(), new FakeJsonStore<AppState>(), new FakeJsonStore<KnownPaths>());
        first.ThrowIfFailed();
        Assert.Equal(["/a.command", "/b.command"], first.Hidden.Paths);
        Assert.Equal(["/a.command", "/b.command"], AppStores.Hidden().Load().Paths);
        Assert.Equal(["theme", "unknown"], ReadKeys());
        Assert.Equal(ThemePreference.Dark, first.Config.Theme);

        var config = File.ReadAllText(ConfigPath);
        var second = new StartupStores(new ConfigStore(), AppStores.Hidden(), new FakeJsonStore<AppState>(), new FakeJsonStore<KnownPaths>());
        Assert.Null(second.ConfigStore.MovedHidden);
        Assert.Equal(["/a.command", "/b.command"], second.Hidden.Paths);
        Assert.Equal(config, File.ReadAllText(ConfigPath));
    }

    // A move cut off after its first write left the list in both files; the next launch joins them.
    [Fact]
    public void Startup_joins_a_hidden_list_left_in_both_files()
    {
        File.WriteAllText(ConfigPath, """{"formatVersion":1,"hidden":["/a.command","/b.command"]}""");
        File.WriteAllText(Path.Combine(_root, "hidden.json"), """{"formatVersion":1,"paths":["/a.command","/c.command"]}""");
        var loaded = new StartupStores(new ConfigStore(), AppStores.Hidden(), new FakeJsonStore<AppState>(), new FakeJsonStore<KnownPaths>());
        Assert.Equal(["/a.command", "/c.command", "/b.command"], loaded.Hidden.Paths);
        Assert.Equal(["/a.command", "/c.command", "/b.command"], AppStores.Hidden().Load().Paths);
        Assert.Empty(ReadKeys());
    }

    // A failed hidden.json write leaves the list in config.json, where later saves keep it for the next try.
    [Fact]
    public async Task A_failed_move_keeps_the_hidden_list_in_config_json()
    {
        File.WriteAllText(ConfigPath, """{"formatVersion":1,"hidden":["/a.command"]}""");
        var store = new ConfigStore();
        var loaded = new StartupStores(store, new FakeJsonStore<HiddenScripts> { ThrowOnSave = true },
            new FakeJsonStore<AppState>(), new FakeJsonStore<KnownPaths>());
        loaded.ThrowIfFailed();
        Assert.Equal(["/a.command"], loaded.Hidden.Paths); // hidden this session all the same
        loaded.Config.Theme = ThemePreference.Dark;
        await store.SaveAsync(loaded.Config, ["theme"]);
        Assert.Equal("""["/a.command"]""", ReadRaw("hidden"));
    }

    private MainWindowViewModel NewViewModel(ConfigStore store, AppConfig config)
    {
        var hiddenStore = AppStores.Hidden();
        var vm = new MainWindowViewModel(store, new FakeJsonStore<AppState>(), new FakeJsonStore<KnownPaths>(), hiddenStore, new FakeRecordStore(), config, new AppState(), new KnownPaths(), hiddenStore.Load(), new ScriptScanner(), new FakeProcessRunner());
        _vms.Add(vm);
        return vm;
    }

    // One stored value, compactly, as the file holds it.
    private string ReadRaw(string key)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(ConfigPath));
        return JsonSerializer.Serialize(document.RootElement.GetProperty(key));
    }

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
