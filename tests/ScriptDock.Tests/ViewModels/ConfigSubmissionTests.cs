using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.Storage;
using ScriptDock.Tests.Fakes;
using ScriptDock.Tests.Storage;
using ScriptDock.ViewModels;
using Xunit;

namespace ScriptDock.Tests.ViewModels;

[Collection(StorageRootEnvironment.CollectionName)]
public sealed class ConfigSubmissionTests : IAsyncLifetime
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "scriptdock-config-" + NanoId.New());
    private readonly string? _previousHome = Environment.GetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable);
    private readonly List<Task> _work = [];
    private readonly List<TaskCompletionSource> _gates = [];
    private readonly Holds _holds = new();
    private HeldConfigStore _store = null!;
    private HiddenScripts _hidden = new();
    private readonly FakeTimeProvider _clock = new();
    private MainWindowViewModel? _vm;
    private AppConfig _config = new();

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, _home);
        _store = new HeldConfigStore(_holds);
        var hiddenStore = new HeldHiddenStore(_holds);
        _config = _store.Load();
        _hidden = hiddenStore.Load();
        _vm = new MainWindowViewModel(_store, new FakeJsonStore<AppState>(), new FakeJsonStore<KnownPaths>(),
            hiddenStore,
            new FakeRecordStore(), _config, new AppState(), new KnownPaths(), _hidden, new ScriptScanner(), new FakeProcessRunner())
            { Time = _clock };
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            foreach (var gate in _gates) gate.TrySetResult();
            // A failed test or caller deadline does not settle a primary writer. Keep relocation
            // until every owned task settles, and run every cleanup even when an earlier one faults.
            try { await Task.WhenAll(_work); }
            finally
            {
                try { if (_vm is not null) await _vm.ShutdownAsync(); }
                finally { BackupStore.Close(); }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, _previousHome);
            if (Directory.Exists(_home)) Directory.Delete(_home, true);
        }
    }

    private TaskCompletionSource HoldNextSave()
    {
        var gate = new TaskCompletionSource();
        _gates.Add(gate);
        _holds.Gates.Enqueue(gate);
        return gate;
    }

    private T Track<T>(T work) where T : Task { _work.Add(work); return work; }
    private Task Hide() => Track(_vm!.ToggleHiddenCommand.ExecuteAsync(new ScriptItem(Path.Combine(_home, "script.command"))));

    [AvaloniaFact]
    public async Task SaveIncludesThePickedFolderWhoseIdentityIsStillResolving()
    {
        var draft = _vm!.CreateSettingsDraft();
        draft.Theme = ThemePreference.Dark;
        var path = Path.Combine(_home, "picked");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var picked = Track(draft.AddPickedRootDirAsync(path, value =>
        {
            entered.Set(); release.Wait(); return value;
        }));
        Task<bool>? saved = null;
        try
        {
            entered.Wait(TestContext.Current.CancellationToken);
            saved = Track(_vm.TryApplySettingsAsync(draft));
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { });
            Assert.False(saved.IsCompleted);
            Assert.Equal(0, _holds.Submissions);
        }
        finally
        {
            release.Set();
            await picked;
            if (saved is not null) await saved;
        }
        Assert.True(await saved!);
        Assert.Equal([path], new ConfigStore().Load().RootDirs);
        Assert.Equal([path], _config.RootDirs);
    }

    [AvaloniaFact]
    public async Task QuitDuringPickedFolderResolutionPreventsALateSettingsSubmission()
    {
        var draft = _vm!.CreateSettingsDraft();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var pick = Track(draft.AddPickedRootDirAsync(Path.Combine(_home, "picked"), value =>
        {
            entered.Set(); release.Wait(); return value;
        }));
        Task<bool>? save = null;
        try
        {
            entered.Wait(TestContext.Current.CancellationToken);
            save = Track(_vm.TryApplySettingsAsync(draft));
            Assert.False(save.IsCompleted);
            _vm.BeginShutdown();
        }
        finally
        {
            release.Set();
            await pick;
            if (save is not null) await save;
        }
        Assert.False(await save!);
        Assert.Equal(0, _holds.Submissions);
        Assert.Empty(_config.RootDirs);
    }

    [AvaloniaFact]
    public async Task SettingsSaveKeepsTheHideWhoseSettlementItOverlaps()
    {
        var gate = HoldNextSave();
        var hide = Hide();
        var draft = _vm!.CreateSettingsDraft();
        draft.UiFontFamily = "Helvetica";
        var settings = Track(_vm.TryApplySettingsAsync(draft));
        draft.UiFontFamily = "later edit";
        gate.SetResult();
        await Task.WhenAll(hide, settings).WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(await settings);
        var loaded = new ConfigStore().Load();
        Assert.Equal([Path.Combine(_home, "script.command")], AppStores.Hidden().Load().Paths);
        Assert.Equal(_hidden.Paths, AppStores.Hidden().Load().Paths);
        Assert.Equal("Helvetica", loaded.UiFontFamily);
        Assert.Equal(_config.UiFontFamily, loaded.UiFontFamily);
    }

    [AvaloniaFact]
    public async Task FailedPredecessorIsNotAdoptedAndDoesNotPoisonTheNextSettingsSave()
    {
        var gate = HoldNextSave();
        _holds.FailNext = true;
        var hide = Hide();
        var draft = _vm!.CreateSettingsDraft();
        draft.Theme = ThemePreference.Dark;
        var settings = Track(_vm.TryApplySettingsAsync(draft));
        gate.SetResult();
        await Task.WhenAll(hide, settings).WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(await settings);
        Assert.Empty(_hidden.Paths);
        var loaded = new ConfigStore().Load();
        Assert.Empty(AppStores.Hidden().Load().Paths);
        Assert.Equal(ThemePreference.Dark, loaded.Theme);
        Assert.Equal(_config.Theme, loaded.Theme);
    }

    [AvaloniaFact]
    public async Task QuitWaitIncludesAChangeAdmittedDuringTheEarlierSave()
    {
        var first = HoldNextSave();
        var second = HoldNextSave();
        var hide = Hide();
        await _holds.LastPublication.WaitAsync(TestContext.Current.CancellationToken);
        _vm!.BeginQuit();
        var quit = Track(_vm.SettingsSavedForQuitAsync());
        var draft = _vm.CreateSettingsDraft();
        draft.Theme = ThemePreference.Dark;
        var settings = Track(_vm.TryApplySettingsAsync(draft));
        first.SetResult();
        await hide.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(quit.IsCompleted);
        _clock.Advance(ExitDeadline.Length);
        Assert.False(await quit.WaitAsync(TestContext.Current.CancellationToken));
        second.SetResult();
        await settings;
        Assert.True(await _vm.SettingsSavedForQuitAsync());
        Assert.True(await settings);
    }

    [AvaloniaFact]
    public async Task RetryOfAnUnresolvedSaveWaitsForTheSameSubmission()
    {
        var gate = HoldNextSave();
        var hide = Hide();
        _vm!.BeginQuit();
        var quit = Track(_vm.SettingsSavedForQuitAsync());
        _clock.Advance(ExitDeadline.Length);
        Assert.False(await quit);
        _vm.BeginQuit(); // the user's Retry starts a new round of the quit
        var retry = Track(_vm.SettingsSavedForQuitAsync(retry: true));
        gate.SetResult();
        Assert.True(await retry.WaitAsync(TestContext.Current.CancellationToken));
        await hide;
        Assert.Equal(1, _holds.Submissions);
        Assert.Equal(_hidden.Paths, AppStores.Hidden().Load().Paths);
    }

    [AvaloniaFact]
    public async Task CommittedShutdownRejectsNewConfigChanges()
    {
        _vm!.BeginShutdown();
        await Hide();
        var draft = _vm.CreateSettingsDraft();
        draft.Theme = ThemePreference.Dark;
        Assert.False(await _vm.TryApplySettingsAsync(draft));
        Assert.Equal(0, _holds.Submissions);
        Assert.Empty(_hidden.Paths);
        Assert.Equal(ThemePreference.System, _config.Theme);
    }

    // Uses the real writers. Holding their completion keeps publication separate from the VM's adoption,
    // so the regression cannot rely on continuation order or fast local filesystem timing. The settings
    // and the hidden list share one queue of holds, as they share one owner in the view model.
    private sealed class Holds
    {
        public Queue<TaskCompletionSource> Gates { get; } = new();
        public int Submissions { get; private set; }
        public bool FailNext { get; set; }
        public Task LastPublication { get; private set; } = Task.CompletedTask;

        public Task Save(Func<Task> write)
        {
            Submissions++;
            if (FailNext)
            {
                FailNext = false;
                return Fail(Gates.Dequeue().Task);
            }
            var published = LastPublication = write();
            return Gates.TryDequeue(out var gate) ? Hold(published, gate.Task) : published;
        }
        private static async Task Hold(Task write, Task gate) { await write; await gate; }
        private static async Task Fail(Task gate) { await gate; throw new IOException("Held save failed before publication."); }
    }

    private sealed class HeldConfigStore(Holds holds) : IConfigStore
    {
        private readonly ConfigStore _inner = new();
        public AppConfig Load() => _inner.Load();
        public IReadOnlyList<string> KeptKeys => _inner.KeptKeys;
        public IReadOnlyList<string>? MovedHidden => _inner.MovedHidden;
        public Task SaveAsync(AppConfig value, IReadOnlyCollection<string>? replaced = null) =>
            holds.Save(() => _inner.SaveAsync(value, replaced));
    }

    private sealed class HeldHiddenStore(Holds holds) : IJsonStore<HiddenScripts>
    {
        private readonly JsonStore<HiddenScripts> _inner = AppStores.Hidden();
        public HiddenScripts Load() => _inner.Load();
        public Task SaveAsync(HiddenScripts value) => holds.Save(() => _inner.SaveAsync(value));
    }
}
