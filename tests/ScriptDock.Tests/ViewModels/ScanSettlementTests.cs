using System;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using System.Linq;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Threading;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.Storage;
using ScriptDock.Tests.Fakes;
using ScriptDock.ViewModels;
using Xunit;

namespace ScriptDock.Tests.ViewModels;

public sealed class ScanSettlementTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scriptdock-scan-" + Guid.NewGuid());
    private readonly HeldStore _store = new();
    private readonly KnownPaths _known = new() { Paths = [] };
    private readonly List<Task> _work = [];
    private MainWindowViewModel? _vm;

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _store.ReleaseAll();
        try { await Task.WhenAll(_work); }
        finally { if (_vm is not null) await _vm.ShutdownAsync(); }
        Directory.Delete(_root, true);
    }

    private MainWindowViewModel Build(TimeProvider? time = null, FakeRecordStore? records = null)
    {
        var config = new AppConfig { RootDirs = [_root], Extensions = [".command"] };
        return _vm = new MainWindowViewModel(new FakeConfigStore { Value = config },
            new FakeJsonStore<AppState>(), _store, new FakeJsonStore<HiddenScripts>(), records ?? new FakeRecordStore(), config, new AppState(),
            _known, new HiddenScripts(), new ScriptScanner(), new FakeProcessRunner()) { Time = time ?? TimeProvider.System };
    }

    private static ScriptFlag FlagOf(MainWindowViewModel vm, string name) =>
        vm.Scripts.Single(script => Path.GetFileName(script.Path) == name).Flag;

    [AvaloniaFact]
    public async Task OlderRecentPreparationCannotReplaceAnAcceptedScansIdentity()
    {
        var path = Path.Combine(_root, "script.command");
        File.WriteAllText(path, "# fixture");
        var records = new FakeRecordStore();
        records.Runs.Add(new RunRecord("fixture", 1, DateTimeOffset.UtcNow, path, null, null, null));
        var vm = Build(records: records);
        var recentEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var followingScanEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseRecent = new ManualResetEventSlim();
        using var releaseScan = new ManualResetEventSlim();
        var probes = 0;
        string Resolve(string value)
        {
            if (value != path) return value;
            var probe = Interlocked.Increment(ref probes);
            if (probe == 1)
            {
                recentEntered.TrySetResult(); releaseRecent.Wait(); return path + "-older-target";
            }
            if (probe == 3) { followingScanEntered.TrySetResult(); releaseScan.Wait(); }
            return path + "-current-target";
        }
        var keys = new PathKeyCache(Resolve);
        typeof(MainWindowViewModel).GetField("_keys", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, keys);
        var startup = Track(vm.InitializeAsync());
        Task? manual = null;
        try
        {
            await recentEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
            manual = Track(vm.RescanCommand.ExecuteAsync(null));
            (await _store.NextAsync()).Settled.SetResult();
            await manual;
            Assert.Equal(path + "-current-target", keys.Key(path));
            releaseRecent.Set();
            await followingScanEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(path + "-current-target", keys.Key(path));
        }
        finally
        {
            releaseRecent.Set(); releaseScan.Set(); _store.ReleaseAll();
            await startup;
            if (manual is not null) await manual;
        }
    }

    [AvaloniaFact]
    public async Task SupersededIdentityProbeCannotOverwriteTheAcceptedCatalogKeys()
    {
        var path = Path.Combine(_root, "script.command");
        File.WriteAllText(path, "# fixture");
        var vm = Build();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var probes = 0;
        string Resolve(string value)
        {
            if (value != path) return value;
            if (Interlocked.Increment(ref probes) == 1)
            {
                entered.TrySetResult();
                release.Wait();
                return path + "-superseded-target";
            }
            return path + "-current-target";
        }
        var keys = new PathKeyCache(Resolve);
        typeof(MainWindowViewModel).GetField("_keys", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, keys);
        var earlier = Track(vm.RescanCommand.ExecuteAsync(null));
        Task? current = null;
        try
        {
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            current = Track(vm.RescanCommand.ExecuteAsync(null));
            (await _store.NextAsync()).Settled.SetResult();
            await current.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(path + "-current-target", keys.Key(path));
        }
        finally
        {
            release.Set();
            _store.ReleaseAll();
            await earlier;
            if (current is not null) await current;
        }
        Assert.Equal(path + "-current-target", keys.Key(path));
    }

    [AvaloniaFact]
    public async Task InaccessibleSubtreeKeepsItsBaselineWhileObservedChangesStillApply()
    {
        var subtree = Path.Combine(_root, "offline");
        var parked = Path.Combine(_root, "parked");
        Directory.CreateDirectory(subtree);
        var old = Path.Combine(subtree, "old.command");
        var deleted = Path.Combine(_root, "offline-neighbour.command");
        File.WriteAllText(old, "# fixture");
        File.WriteAllText(deleted, "# fixture");
        _known.Paths = [old, deleted];
        var config = new AppConfig { RootDirs = [subtree, _root], Extensions = [".command"], IgnorePatterns = [new("parked/")] };
        var vm = _vm = new MainWindowViewModel(new FakeConfigStore { Value = config },
            new FakeJsonStore<AppState>(), _store, new FakeJsonStore<HiddenScripts>(), new FakeRecordStore(), config, new AppState(), _known,
            new HiddenScripts(),
            new ScriptScanner(), new FakeProcessRunner());

        Directory.Move(subtree, parked); // The configured root is unavailable, not evidence of deletion.
        File.Delete(deleted);
        var added = Path.Combine(_root, "added.command");
        File.WriteAllText(added, "# fixture");
        for (var pass = 0; pass < 2; pass++)
        {
            var scan = Track(vm.RescanCommand.ExecuteAsync(null));
            var held = await _store.NextAsync();
            held.Settled.SetResult();
            await scan;
            Assert.Contains(old, _known.Paths!);
            Assert.Contains(added, _known.Paths!);
            Assert.DoesNotContain(deleted, _known.Paths!);
            Assert.NotEqual(ScriptFlag.Removed, FlagOf(vm, "old.command"));
            Assert.True(vm.HasOperationalError);
        }

        Directory.Move(parked, subtree);
        var recovered = Track(vm.RescanCommand.ExecuteAsync(null));
        (await _store.NextAsync()).Settled.SetResult();
        await recovered;
        Assert.Equal(ScriptFlag.None, FlagOf(vm, "old.command"));
        Assert.False(vm.HasOperationalError);

        File.Delete(old);
        var observedDeletion = Track(vm.RescanCommand.ExecuteAsync(null));
        (await _store.NextAsync()).Settled.SetResult();
        await observedDeletion;
        Assert.Equal(ScriptFlag.Removed, FlagOf(vm, "old.command"));
        Assert.DoesNotContain(old, _known.Paths!);
    }

    [AvaloniaFact]
    public async Task SavedRecentPathsArePreparedBeforeTheirFirstRebuild()
    {
        var path = Path.Combine(_root, "previous.command");
        var records = new FakeRecordStore();
        records.Runs.Add(new RunRecord("earlier", 1, DateTimeOffset.UtcNow, path, null, null, null));
        var vm = Build(records: records);
        await vm.LoadRecentAsync();
        Assert.Empty(vm.Recent); // The projection has not had a chance to fill a cache miss itself.
        Assert.True(PreparedKeys(vm).ContainsKey(path));
    }

    [AvaloniaFact]
    public async Task FirstScanPreparesRemovedPathsBeforePublishingItsTiles()
    {
        var path = Path.Combine(_root, "gone.command");
        _known.Paths = [path];
        var vm = Build();
        string Resolve(string value)
        {
            Assert.False(Dispatcher.UIThread.CheckAccess());
            return PathIdentity.Key(value);
        }
        var keys = new PathKeyCache(Resolve);
        typeof(MainWindowViewModel).GetField("_keys", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, keys);
        var scan = Track(vm.RescanCommand.ExecuteAsync(null));
        var held = await _store.NextAsync();
        Assert.Empty(vm.Scripts);
        Assert.False(PreparedKeys(vm).ContainsKey(path)); // Unaccepted preparation is still local.
        held.Settled.SetResult();
        await scan.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ScriptFlag.Removed, FlagOf(vm, "gone.command"));
    }

    // Inspect the actual cache before projection. Resolving a miss from the UI would populate it
    // too, so inspecting only the finished list would not detect this regression.
    private static Dictionary<string, string> PreparedKeys(MainWindowViewModel vm)
    {
        var cache = typeof(MainWindowViewModel).GetField("_keys", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        return (Dictionary<string, string>)typeof(PathKeyCache)
            .GetField("_keys", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
    }

    [AvaloniaFact]
    public async Task RunDuringAFullScansSave_KeepsItsNewFlagEnded()
    {
        var path = Path.Combine(_root, "a.command");
        File.WriteAllText(path, "# fixture");
        var vm = Build();
        var scan = Track(vm.RescanCommand.ExecuteAsync(null));
        var held = await _store.NextAsync();

        await vm.RunScriptCommand.ExecuteAsync(new ScriptItem(path));
        held.Settled.SetResult();
        await scan.WaitAsync(TestContext.Current.CancellationToken);

        Assert.NotEqual(ScriptFlag.New, FlagOf(vm, "a.command"));
    }

    [AvaloniaFact]
    public async Task RunDuringABackgroundScansSave_KeepsItsNewFlagEnded()
    {
        var a = Path.Combine(_root, "a.command");
        File.WriteAllText(a, "# fixture");
        var clock = new FakeTimeProvider();
        var vm = Build(clock);
        var initial = Track(vm.InitializeAsync());
        (await _store.NextAsync()).Settled.SetResult();
        await initial.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ScriptFlag.New, FlagOf(vm, "a.command"));

        File.WriteAllText(Path.Combine(_root, "b.command"), "# fixture");
        vm.OnWindowActivated();
        clock.Advance(MainWindowViewModel.ActivationRescanDelay);
        Dispatcher.UIThread.RunJobs();
        var held = await _store.NextAsync();
        await vm.RunScriptCommand.ExecuteAsync(new ScriptItem(a));
        held.Settled.SetResult();
        await vm.ActivationScan.WaitAsync(TestContext.Current.CancellationToken);

        Assert.NotEqual(ScriptFlag.New, FlagOf(vm, "a.command"));
        Assert.Equal(ScriptFlag.New, FlagOf(vm, "b.command"));
    }

    private Task Track(Task task) { _work.Add(task); return task; }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupersededSaveCannotReplaceNewerScanStatus(bool oldSaveFails)
    {
        File.WriteAllText(Path.Combine(_root, "a.command"), "# fixture");
        var vm = Build();
        var initial = Track(vm.InitializeAsync());
        var oldSave = await _store.NextAsync();
        Assert.Empty(vm.Scripts);
        Assert.Empty(_known.Paths!);
        File.WriteAllText(Path.Combine(_root, "b.command"), "# fixture");
        var newer = Track(vm.RescanCommand.ExecuteAsync(null));
        var currentSave = await _store.NextAsync();
        Assert.NotSame(oldSave.Value, currentSave.Value);
        Assert.Single(oldSave.Value.Paths!);
        Assert.Equal(2, currentSave.Value.Paths!.Count);
        if (oldSaveFails) oldSave.Settled.SetException(new IOException("old save failed"));
        else oldSave.Settled.SetResult();
        await initial.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(vm.IsScanning);
        Assert.Equal("scan.running", vm.CatalogResultMessage?.Key);
        Assert.False(vm.HasOperationalError);
        Assert.Empty(vm.Scripts);
        Assert.Empty(_known.Paths!);
        currentSave.Settled.SetResult();
        await newer.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(vm.IsScanning);
        Assert.Equal(2, vm.Scripts.Count);
        Assert.Equal(2, _known.Paths!.Count);
    }

    [AvaloniaFact]
    public async Task FailedSaveDoesNotAdvanceTheBaselineOrVisibleCatalog()
    {
        File.WriteAllText(Path.Combine(_root, "new.command"), "# fixture");
        var vm = Build();
        var first = Track(vm.RescanCommand.ExecuteAsync(null));
        var failed = await _store.NextAsync();
        failed.Settled.SetException(new IOException("save failed"));
        await first.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Empty(_known.Paths!);
        Assert.Empty(vm.Scripts);
        Assert.Equal("scan.failed", vm.OperationalErrorMessage?.Key);
        var retry = Track(vm.RescanCommand.ExecuteAsync(null));
        var saved = await _store.NextAsync();
        saved.Settled.SetResult();
        await retry.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ScriptFlag.New, Assert.Single(vm.Scripts).Flag);
        Assert.Single(_known.Paths!);
        Assert.False(vm.HasOperationalError);
    }

    [AvaloniaFact]
    public async Task SettingsChangedDuringSaveKeepTheirRescanNoticeAndRejectOldCatalog()
    {
        File.WriteAllText(Path.Combine(_root, "old.command"), "# fixture");
        var vm = Build();
        var scan = Track(vm.RescanCommand.ExecuteAsync(null));
        var held = await _store.NextAsync();
        var draft = vm.CreateSettingsDraft();
        // Changing the extensions changes the scan inputs while its durable wait is held.
        draft.Extensions.Clear();
        draft.Extensions.Add(new EditableItem(".ps1"));
        Assert.True(await vm.TryApplySettingsAsync(draft));
        held.Settled.SetResult();
        await scan.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Empty(vm.Scripts);
        Assert.Empty(_known.Paths!);
        Assert.Equal("scan.configChanged", vm.CatalogResultMessage?.Key);
    }

    private sealed record Save(KnownPaths Value, TaskCompletionSource Settled);

    private sealed class HeldStore : IJsonStore<KnownPaths>
    {
        private readonly Channel<Save> _submitted = Channel.CreateUnbounded<Save>();
        private readonly List<Save> _owned = [];
        private bool _released;
        public KnownPaths Load() => new();
        public Task SaveAsync(KnownPaths value)
        {
            var save = new Save(value, new TaskCompletionSource());
            _owned.Add(save);
            _submitted.Writer.TryWrite(save);
            if (_released) save.Settled.TrySetResult();
            return save.Settled.Task;
        }
        public Task<Save> NextAsync() => _submitted.Reader.ReadAsync().AsTask().WaitAsync(TestContext.Current.CancellationToken);
        public void ReleaseAll()
        {
            _released = true;
            foreach (var save in _owned) save.Settled.TrySetResult();
        }
    }
}
