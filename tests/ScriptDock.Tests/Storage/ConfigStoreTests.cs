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
    public async Task Dialog_WritesEverySetThatDiffers_KeepsUnknownKeysAndDropsRetiredOnes()
    {
        // The retired process settings are dropped; keys this version does not know are kept unchanged.
        File.WriteAllText(ConfigPath, """{"formatVersion":1,"hidden":["/hidden"],"extensions":[],"version":1,"unknown":true,"killProcessesOnClose":true,"recaptureProcessesOnLaunch":false}""");
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
        Assert.Equal(["/hidden"], store.Load().Hidden);
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
    public async Task InvalidSet_IsKeptUnchangedAtTheNextSave()
    {
        File.WriteAllText(ConfigPath, """{"formatVersion":1,"extensions":[null,".sh"],"theme":"future","hidden":["/ok"]}""");
        var store = new ConfigStore();
        var config = store.Load();
        Assert.Equal(["extensions", "theme"], store.KeptKeys.Order());
        config.Hidden.Add("/new");
        await store.SaveAsync(config);
        Assert.Equal(["extensions", "hidden", "theme"], ReadKeys());
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
        draft.Extensions.Clear();
        draft.Extensions.Add(new EditableItem(".py"));
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
        await vm.ToggleHiddenCommand.ExecuteAsync(new ScriptItem("/scripts/run.command"));
        Assert.Equal("""[null,".sh"]""", ReadRaw("extensions"));
        Assert.Equal(["extensions", "hidden"], ReadKeys());
        await vm.ShutdownAsync();
    }

    [Fact]
    public async Task QueuedReset_DoesNotResurrectTheSetReplacedByThePreviousSave()
    {
        File.WriteAllText(ConfigPath, """{"formatVersion":1,"extensions":[null,".sh"]}""");
        var store = new ConfigStore();
        var config = store.Load();
        // Hold the actual primary-write queue so both snapshots precede publication. Without this
        // gate the first write could finish before the reset, hiding a premature merge of kept keys.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var primary = typeof(ConfigStore).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        typeof(JsonStore<Dictionary<string, JsonElement>>)
            .GetField("_queueTail", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(primary, release.Task);
        config.Extensions = [".py"];
        var replacement = store.SaveAsync(config);
        config.Extensions = [ConfigDefaults.DefaultExtension];
        var reset = store.SaveAsync(config);
        try
        {
            Assert.False(replacement.IsCompleted);
            Assert.False(reset.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(replacement, reset);
        }
        Assert.Empty(store.KeptKeys);
        Assert.Empty(ReadKeys());
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

    private MainWindowViewModel NewViewModel(ConfigStore store, AppConfig config)
    {
        var vm = new MainWindowViewModel(store, new FakeJsonStore<AppState>(), new FakeJsonStore<KnownPaths>(), new FakeRecordStore(), config, new AppState(), new KnownPaths(), new ScriptScanner(), new FakeProcessRunner());
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
