using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Channels;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.Storage;
using ScriptDock.Tests.Fakes;
using ScriptDock.ViewModels;
using Xunit;

namespace ScriptDock.Tests.ViewModels;

public sealed class ScanSettlementTests : IAsyncLifetime
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
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
        await Task.WhenAll(_work).WaitAsync(Bound, TestContext.Current.CancellationToken);
        if (_vm is not null) await _vm.ShutdownAsync();
        Directory.Delete(_root, true);
    }

    private MainWindowViewModel Build()
    {
        var config = new AppConfig { RootDirs = [_root], Extensions = [".command"] };
        return _vm = new MainWindowViewModel(new FakeConfigStore { Value = config },
            new FakeJsonStore<AppState>(), _store, new FakeRecordStore(), config, new AppState(),
            _known, new ScriptScanner(), new FakeProcessRunner());
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
        await initial.WaitAsync(Bound, TestContext.Current.CancellationToken);
        Assert.True(vm.IsScanning);
        Assert.Equal("scan.running", vm.CatalogResultMessage?.Key);
        Assert.False(vm.HasOperationalError);
        Assert.Empty(vm.Scripts);
        Assert.Empty(_known.Paths!);
        currentSave.Settled.SetResult();
        await newer.WaitAsync(Bound, TestContext.Current.CancellationToken);
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
        await first.WaitAsync(Bound, TestContext.Current.CancellationToken);
        Assert.Empty(_known.Paths!);
        Assert.Empty(vm.Scripts);
        Assert.Equal("scan.failed", vm.OperationalErrorMessage?.Key);
        var retry = Track(vm.RescanCommand.ExecuteAsync(null));
        var saved = await _store.NextAsync();
        saved.Settled.SetResult();
        await retry.WaitAsync(Bound, TestContext.Current.CancellationToken);
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
        draft.Extensions.Add(".ps1");
        Assert.True(await vm.TryApplySettingsAsync(draft));
        held.Settled.SetResult();
        await scan.WaitAsync(Bound, TestContext.Current.CancellationToken);
        Assert.Empty(vm.Scripts);
        Assert.Empty(_known.Paths!);
        Assert.Equal("scan.configChanged", vm.CatalogResultMessage?.Key);
    }

    private sealed record Save(KnownPaths Value, TaskCompletionSource Settled);

    private sealed class HeldStore : IJsonStore<KnownPaths>
    {
        private readonly Channel<Save> _submitted = Channel.CreateUnbounded<Save>();
        private readonly List<Save> _owned = [];
        public KnownPaths Load() => new();
        public void Save(KnownPaths value) => throw new NotSupportedException();
        public Task SaveAsync(KnownPaths value)
        {
            var save = new Save(value, new TaskCompletionSource());
            _owned.Add(save);
            _submitted.Writer.TryWrite(save);
            return save.Settled.Task;
        }
        public Task<Save> NextAsync() => _submitted.Reader.ReadAsync().AsTask().WaitAsync(Bound, TestContext.Current.CancellationToken);
        public void ReleaseAll() { foreach (var save in _owned) save.Settled.TrySetResult(); }
    }
}
