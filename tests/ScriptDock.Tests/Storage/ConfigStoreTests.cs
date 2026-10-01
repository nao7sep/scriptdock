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
    public async Task UnchangedDialog_DoesNotWriteAFile()
    {
        var store = new ConfigStore();
        var config = store.Load();
        var vm = new MainWindowViewModel(store, new FakeJsonStore<AppState>(), config,
            new AppState(), new ScriptScanner(), new FakeProcessRunner());
        Assert.True(await vm.TryApplySettingsAsync(new SettingsDialogViewModel(config)));
        Assert.False(File.Exists(ConfigPath));
    }

    [Fact]
    public async Task Hide_WritesExactlyHiddenAndLeavesOtherSetsAbsent()
    {
        var store = new ConfigStore();
        var config = store.Load();
        var vm = new MainWindowViewModel(store, new FakeJsonStore<AppState>(), config,
            new AppState(), new ScriptScanner(), new FakeProcessRunner());
        await vm.ToggleHiddenCommand.ExecuteAsync(new ScriptItem("/scripts/run.command"));
        Assert.Equal([ConfigSets.Hidden], ReadKeys());
        var loaded = store.Load();
        Assert.Equal(["/scripts/run.command"], loaded.Hidden);
        Assert.Equal([ConfigDefaults.DefaultExtension], loaded.Extensions);
        Assert.Equal(ConfigDefaults.BuiltInIgnorePatterns, loaded.IgnorePatterns);
        Assert.Equal("", loaded.UiFontFamily);
        Assert.Equal("system", loaded.Language);
        Assert.Equal(ThemePreference.System, loaded.Theme);
        Assert.Empty(loaded.RootDirs);
        Assert.False(loaded.KillProcessesOnClose);
        Assert.True(loaded.RecaptureProcessesOnLaunch);
    }

    [Fact]
    public async Task Dialog_WritesOnlyChangedSetsAndPreservesExistingCopies()
    {
        File.WriteAllText(ConfigPath, """{"hidden":["/hidden"],"extensions":[],"version":1,"unknown":true}""");
        var store = new ConfigStore();
        var config = store.Load();
        var vm = new MainWindowViewModel(store, new FakeJsonStore<AppState>(), config,
            new AppState(), new ScriptScanner(), new FakeProcessRunner());
        var draft = new SettingsDialogViewModel(config) { KillProcessesOnClose = true };
        Assert.True(await vm.TryApplySettingsAsync(draft));
        Assert.Equal(["extensions", "hidden", "killProcessesOnClose"], ReadKeys());
        Assert.Empty(store.Load().Extensions);
        Assert.Equal(["/hidden"], store.Load().Hidden);
    }

    [Fact]
    public async Task Reset_DeletesCopiesIncludingAnExplicitCopyEqualToTheBuiltIn()
    {
        var store = new ConfigStore();
        var config = store.Load();
        await store.SaveSetsAsync(config, [ConfigSets.Extensions, ConfigSets.IgnorePatterns]);
        config = store.Load();
        var vm = new MainWindowViewModel(store, new FakeJsonStore<AppState>(), config,
            new AppState(), new ScriptScanner(), new FakeProcessRunner());
        var draft = new SettingsDialogViewModel(config);
        draft.ResetExtensions();
        draft.ResetIgnorePatterns();
        Assert.True(draft.IsDirty);
        Assert.True(await vm.TryApplySettingsAsync(draft));
        Assert.Empty(ReadKeys());
        Assert.Empty(config.StoredSetKeys);
        Assert.Equal([ConfigDefaults.DefaultExtension], store.Load().Extensions);
        Assert.Equal(ConfigDefaults.BuiltInIgnorePatterns, store.Load().IgnorePatterns);
    }

    [Fact]
    public void InvalidSet_UsesBuiltInWithoutQuarantiningOrChangingOtherSets()
    {
        const string json = """{"extensions":[null,".sh"],"theme":"future","language":"unknown","hidden":["/ok"]}""";
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
    public async Task OverlappingWrites_SnapshotValuesAndPreserveIndependentSets()
    {
        var store = new ConfigStore();
        var config = store.Load();
        config.Hidden.Add("/one");
        var first = store.SaveSetsAsync(config, [ConfigSets.Hidden]);
        config.Hidden.Add("/two");
        var second = store.SaveSetsAsync(config, [ConfigSets.Hidden]);
        config.Extensions = [".sh"];
        var third = store.SaveSetsAsync(config, [ConfigSets.Extensions]);
        config.Extensions.Add(".late");
        await Task.WhenAll(first, second, third);
        Assert.Equal(["/one", "/two"], store.Load().Hidden);
        Assert.Equal([".sh"], store.Load().Extensions);
        Assert.Equal([ConfigSets.Extensions, ConfigSets.Hidden], ReadKeys());
    }

    private string[] ReadKeys()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(ConfigPath));
        return document.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray();
    }
}
