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
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private readonly string _home = Path.Combine(Path.GetTempPath(), "scriptdock-config-" + NanoId.New());
    private readonly string? _previousHome = Environment.GetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable);
    private readonly List<Task> _work = [];
    private readonly List<TaskCompletionSource> _gates = [];
    private HeldConfigStore _store = null!;
    private readonly FakeTimeProvider _clock = new();
    private MainWindowViewModel? _vm;
    private AppConfig _config = new();

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, _home);
        _store = new HeldConfigStore();
        _config = _store.Load();
        _vm = new MainWindowViewModel(_store, new FakeJsonStore<AppState>(), new FakeJsonStore<KnownPaths>(),
            new FakeRecordStore(), _config, new AppState(), new KnownPaths(), new ScriptScanner(), new FakeProcessRunner())
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
        _store.Gates.Enqueue(gate);
        return gate;
    }

    private T Track<T>(T work) where T : Task { _work.Add(work); return work; }
    private Task Hide() => Track(_vm!.ToggleHiddenCommand.ExecuteAsync(new ScriptItem(Path.Combine(_home, "script.command"))));

    [AvaloniaFact]
    public async Task FailedBodyKeepsRelocationUntilTheActualPrimaryWriteSettles()
    {
        // The ambient home is also fixture-owned. A late write may never touch normal developer data.
        var sentinel = Path.Combine(_home, "config.json");
        const string bytes = "{\"formatVersion\":1,\"rootDirs\":[\"/owned-sentinel/\"]}";
        File.WriteAllText(sentinel, bytes);
        var inventory = Directory.GetFileSystemEntries(_home).Order().ToArray();
        UnixFileMode? mode = null;
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead);
            mode = File.GetUnixFileMode(_home);
        }
        var nested = new ConfigSubmissionTests();
        await nested.InitializeAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = typeof(HeldConfigStore).GetField("_inner", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(nested._store)!;
        var primary = typeof(ConfigStore).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(inner)!;
        typeof(JsonStore<Dictionary<string, JsonElement>>).GetField("_queueTail", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(primary, release.Task);
        var write = nested.Hide();
        _ = nested.Track(Task.FromException(new IOException("injected failed body")));
        var cleanup = nested.DisposeAsync().AsTask();
        try
        {
            // Cross the former five-second cleanup deadline while the production writer is held.
            await Task.Delay(TimeSpan.FromSeconds(6), TestContext.Current.CancellationToken);
            Assert.False(cleanup.IsCompleted);
            Assert.Equal(nested._home, StorageRoot.Directory);
        }
        finally
        {
            release.TrySetResult();
            try
            {
                await write;
                try { await cleanup; }
                catch (IOException) { /* the failed body is still reported, after actual settlement */ }
            }
            finally
            {
                // This regression's own cleanup must also be safe while demonstrating the old defect.
                try { await nested._vm!.ShutdownAsync(); }
                finally { BackupStore.Close(); }
            }
        }
        Assert.Equal(_home, StorageRoot.Directory);
        Assert.Equal(bytes, File.ReadAllText(sentinel));
        Assert.Equal(inventory, Directory.GetFileSystemEntries(_home).Order());
        if (mode is { } expected && !OperatingSystem.IsWindows()) Assert.Equal(expected, File.GetUnixFileMode(_home));
    }

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
            Assert.True(entered.Wait(Bound, TestContext.Current.CancellationToken));
            saved = Track(_vm.TryApplySettingsAsync(draft));
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { });
            Assert.False(saved.IsCompleted);
            Assert.Equal(0, _store.Submissions);
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
            Assert.True(entered.Wait(Bound, TestContext.Current.CancellationToken));
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
        Assert.Equal(0, _store.Submissions);
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
        await Task.WhenAll(hide, settings).WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.True(await settings);
        var loaded = new ConfigStore().Load();
        Assert.Equal([Path.Combine(_home, "script.command")], loaded.Hidden);
        Assert.Equal(_config.Hidden, loaded.Hidden);
        Assert.Equal("Helvetica", loaded.UiFontFamily);
        Assert.Equal(_config.UiFontFamily, loaded.UiFontFamily);
    }

    [AvaloniaFact]
    public async Task FailedPredecessorIsNotAdoptedAndDoesNotPoisonTheNextSettingsSave()
    {
        var gate = HoldNextSave();
        _store.FailNext = true;
        var hide = Hide();
        var draft = _vm!.CreateSettingsDraft();
        draft.Theme = ThemePreference.Dark;
        var settings = Track(_vm.TryApplySettingsAsync(draft));
        gate.SetResult();
        await Task.WhenAll(hide, settings).WaitAsync(Bound, TestContext.Current.CancellationToken);
        Assert.True(await settings);
        Assert.Empty(_config.Hidden);
        var loaded = new ConfigStore().Load();
        Assert.Empty(loaded.Hidden);
        Assert.Equal(ThemePreference.Dark, loaded.Theme);
        Assert.Equal(_config.Theme, loaded.Theme);
    }

    [AvaloniaFact]
    public async Task QuitWaitIncludesAChangeAdmittedDuringTheEarlierSave()
    {
        var first = HoldNextSave();
        var second = HoldNextSave();
        var hide = Hide();
        await _store.LastPublication.WaitAsync(Bound, TestContext.Current.CancellationToken);
        var quit = Track(_vm!.SettingsSavedForQuitAsync());
        var draft = _vm.CreateSettingsDraft();
        draft.Theme = ThemePreference.Dark;
        var settings = Track(_vm.TryApplySettingsAsync(draft));
        first.SetResult();
        await hide.WaitAsync(Bound, TestContext.Current.CancellationToken);
        Assert.False(quit.IsCompleted);
        _clock.Advance(MainWindowViewModel.QuitSettingsWriteBound);
        Assert.False(await quit.WaitAsync(Bound, TestContext.Current.CancellationToken));
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
        var quit = Track(_vm!.SettingsSavedForQuitAsync());
        _clock.Advance(MainWindowViewModel.QuitSettingsWriteBound);
        Assert.False(await quit);
        var retry = Track(_vm.SettingsSavedForQuitAsync(retry: true));
        gate.SetResult();
        Assert.True(await retry.WaitAsync(Bound, TestContext.Current.CancellationToken));
        await hide;
        Assert.Equal(1, _store.Submissions);
        Assert.Equal(_config.Hidden, new ConfigStore().Load().Hidden);
    }

    [AvaloniaFact]
    public async Task CommittedShutdownRejectsNewConfigChanges()
    {
        _vm!.BeginShutdown();
        await Hide();
        var draft = _vm.CreateSettingsDraft();
        draft.Theme = ThemePreference.Dark;
        Assert.False(await _vm.TryApplySettingsAsync(draft));
        Assert.Equal(0, _store.Submissions);
        Assert.Empty(_config.Hidden);
        Assert.Equal(ThemePreference.System, _config.Theme);
    }

    // Uses the real writer. Holding its completion keeps publication separate from the VM's adoption,
    // so the regression cannot rely on continuation order or fast local filesystem timing.
    private sealed class HeldConfigStore : IConfigStore
    {
        private readonly ConfigStore _inner = new();
        public Queue<TaskCompletionSource> Gates { get; } = new();
        public int Submissions { get; private set; }
        public bool FailNext { get; set; }
        public Task LastPublication { get; private set; } = Task.CompletedTask;
        public AppConfig Load() => _inner.Load();
        public IReadOnlyList<string> KeptKeys => _inner.KeptKeys;
        public Task SaveAsync(AppConfig value, IReadOnlyCollection<string>? replaced = null)
        {
            Submissions++;
            if (FailNext)
            {
                FailNext = false;
                return Fail(Gates.Dequeue().Task);
            }
            var write = LastPublication = _inner.SaveAsync(value, replaced);
            return Gates.TryDequeue(out var gate) ? Hold(write, gate.Task) : write;
        }
        private static async Task Hold(Task write, Task gate) { await write; await gate; }
        private static async Task Fail(Task gate) { await gate; throw new IOException("Held save failed before publication."); }
    }
}
