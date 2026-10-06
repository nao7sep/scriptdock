using System;
using System.Threading.Tasks;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.Tests.Fakes;
using ScriptDock.ViewModels;
using Xunit;

namespace ScriptDock.Tests.ViewModels;

/// <summary>
/// The quit's own steps, each within its bound on a controlled clock (unsaved-edits-conventions,
/// Quitting): view state never holds a quit, and a step that stalls ends at its bound.
/// </summary>
public sealed class MainWindowViewModelQuitTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(1);

    private readonly FakeTimeProvider _clock = new();
    private readonly FakeConfigStore _configStore = new();
    private readonly FakeJsonStore<AppState> _stateStore = new();
    private readonly FakeProcessRunner _runner = new();

    private MainWindowViewModel NewVm() => new(
        _configStore, _stateStore, new FakeJsonStore<KnownPaths>(), new FakeRecordStore(),
        _configStore.Value, _stateStore.Value, new KnownPaths(), new ScriptScanner(), _runner)
    {
        Time = _clock,
    };

    [Fact]
    public async Task A_stalled_view_state_save_ends_at_its_bound_and_shows_nothing()
    {
        _stateStore.SaveGate = new TaskCompletionSource();
        var vm = NewVm();

        var save = vm.PersistPaneSizesAsync(420, 240);
        _clock.Advance(MainWindowViewModel.QuitStateSaveBound - Tick);
        Assert.False(save.IsCompleted);

        _clock.Advance(Tick);
        await save;
        Assert.False(vm.HasOperationalError);
    }

    [Fact]
    public async Task A_failed_view_state_save_is_logged_only()
    {
        _stateStore.ThrowOnSave = true;
        var vm = NewVm();

        await vm.PersistPaneSizesAsync(420, 240);

        Assert.False(vm.HasOperationalError);
    }

    [Fact]
    public async Task Scripts_still_dying_hold_the_quit_only_until_their_bound()
    {
        _runner.StopAllGate = new TaskCompletionSource();
        var vm = NewVm();

        var shutdown = vm.ShutdownAsync();
        _clock.Advance(MainWindowViewModel.QuitStopScriptsBound - Tick);
        Assert.False(shutdown.IsCompleted);

        _clock.Advance(Tick);
        await shutdown;
        Assert.Equal(1, _runner.StopAllCalls);
        Assert.False(vm.HasOperationalError);
    }
}
