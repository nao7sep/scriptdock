using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.Tests.Fakes;
using ScriptDock.ViewModels;
using Xunit;

namespace ScriptDock.Tests.ViewModels;

/// <summary>
/// The quit's own steps, within its one exit deadline on a controlled clock (unsaved-edits-conventions,
/// Quitting): view state never holds a quit, a step that stalls ends at the deadline, and a settings change
/// the user made is waited for and reported when it did not land. Every gate a test opens is released,
/// the work it started awaited, and each view model shut down at the end, so nothing stays pending after it.
/// </summary>
public sealed class MainWindowViewModelQuitTests : IAsyncLifetime
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(1);

    private readonly FakeTimeProvider _clock = new();
    private readonly FakeConfigStore _configStore = new();
    private readonly FakeJsonStore<AppState> _stateStore = new();
    private readonly FakeJsonStore<HiddenScripts> _hiddenStore = new();
    private readonly FakeProcessRunner _runner = new();
    private readonly List<TaskCompletionSource> _gates = [];
    private readonly List<Task> _work = [];
    private readonly List<MainWindowViewModel> _vms = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        // A gate the test settled keeps its outcome; one left open (a stall at its bound) lands now, so the
        // work waiting on it can finish.
        foreach (var gate in _gates)
            gate.TrySetResult();
        try { await Task.WhenAll(_work); }
        finally
        {
            // Cleanup owns actual settlement even if the failed body has cancelled its token.
            await Task.WhenAll(_vms.Select(vm => vm.ShutdownAsync()));
        }
    }

    private TaskCompletionSource Gate()
    {
        var gate = new TaskCompletionSource();
        _gates.Add(gate);
        return gate;
    }

    private T Track<T>(T work) where T : Task
    {
        _work.Add(work);
        return work;
    }

    private MainWindowViewModel NewVm()
    {
        var vm = new MainWindowViewModel(
            _configStore, _stateStore, new FakeJsonStore<KnownPaths>(), _hiddenStore, new FakeRecordStore(),
            _configStore.Value, _stateStore.Value, new KnownPaths(), _hiddenStore.Value, new ScriptScanner(), _runner)
        {
            Time = _clock,
        };
        _vms.Add(vm);
        return vm;
    }

    [Fact]
    public async Task A_stalled_view_state_save_ends_at_the_exit_deadline_and_shows_nothing()
    {
        _stateStore.SaveGate = Gate();
        var vm = NewVm();
        vm.BeginQuit();

        var save = Track(vm.PersistPaneSizesAsync(420, 240));
        _clock.Advance(ExitDeadline.Length - Tick);
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
    public async Task Scripts_still_dying_hold_the_quit_only_until_what_remains_of_the_deadline()
    {
        _runner.StopAllGate = Gate();
        var vm = NewVm();
        vm.BeginQuit();
        _clock.Advance(TimeSpan.FromSeconds(2)); // spent on the quit's earlier steps

        var shutdown = Track(vm.ShutdownAsync());
        _clock.Advance(ExitDeadline.Length - TimeSpan.FromSeconds(2) - Tick);
        Assert.False(shutdown.IsCompleted);

        _clock.Advance(Tick);
        await shutdown;
        Assert.Equal(1, _runner.StopAllCalls);
        Assert.False(vm.HasOperationalError);
    }

    // Applies a settings change whose save stays running until the test settles the returned gate.
    private TaskCompletionSource ApplyWithSaveRunning(MainWindowViewModel vm, out Task<bool> apply)
    {
        var gate = Gate();
        _configStore.SaveGate = gate;
        var draft = vm.CreateSettingsDraft();
        draft.UiFontFamily = "Helvetica";
        apply = Track(vm.TryApplySettingsAsync(draft));
        return gate;
    }

    [Fact]
    public async Task With_no_settings_write_running_the_settings_are_safe_at_once()
    {
        var vm = NewVm();

        Assert.True(await vm.SettingsSavedForQuitAsync());
    }

    [Fact]
    public async Task A_settings_write_that_failed_before_the_quit_does_not_hold_it()
    {
        _configStore.ThrowOnSave = true;
        var vm = NewVm();
        var draft = vm.CreateSettingsDraft();
        draft.UiFontFamily = "Helvetica";
        Assert.False(await vm.TryApplySettingsAsync(draft)); // shown in the Settings dialog, not adopted

        Assert.True(await vm.SettingsSavedForQuitAsync());
    }

    [Fact]
    public async Task A_settings_write_that_lands_during_the_quit_lets_it_go_on()
    {
        var vm = NewVm();
        var gate = ApplyWithSaveRunning(vm, out var apply);

        var saved = Track(vm.SettingsSavedForQuitAsync());
        Assert.False(saved.IsCompleted);
        gate.SetResult();

        Assert.True(await saved);
        Assert.True(await apply);
    }

    [Fact]
    public async Task A_settings_write_that_fails_during_the_quit_stops_it()
    {
        var vm = NewVm();
        var gate = ApplyWithSaveRunning(vm, out var apply);

        var saved = Track(vm.SettingsSavedForQuitAsync());
        gate.SetException(new IOException("disk full (test)"));

        Assert.False(await saved);
        Assert.False(await apply);
    }

    [Fact]
    public async Task A_hide_whose_write_fails_during_the_quit_stops_it()
    {
        var gate = Gate();
        _hiddenStore.SaveGate = gate;
        var vm = NewVm();
        var hide = Track(vm.ToggleHiddenCommand.ExecuteAsync(new ScriptItem("/x/a.command") { DisplayName = "a" }));

        var saved = Track(vm.SettingsSavedForQuitAsync());
        gate.SetException(new IOException("disk full (test)"));
        await hide;

        Assert.False(await saved);
        Assert.Empty(_hiddenStore.Value.Paths);
    }

    [Fact]
    public async Task A_stalled_settings_write_stops_the_quit_at_the_exit_deadline()
    {
        var vm = NewVm();
        ApplyWithSaveRunning(vm, out _);
        vm.BeginQuit();

        var saved = Track(vm.SettingsSavedForQuitAsync());
        _clock.Advance(ExitDeadline.Length - Tick);
        Assert.False(saved.IsCompleted);

        _clock.Advance(Tick);
        Assert.False(await saved); // the existing Retry / Quit anyway question follows
    }

    [Fact]
    public async Task Retry_saves_the_same_change_again()
    {
        var vm = NewVm();
        var gate = ApplyWithSaveRunning(vm, out var apply);
        var saved = Track(vm.SettingsSavedForQuitAsync());
        gate.SetException(new IOException("disk full (test)"));
        Assert.False(await saved);
        Assert.False(await apply);

        _configStore.SaveGate = null;
        Assert.True(await vm.SettingsSavedForQuitAsync(retry: true));

        Assert.Equal("Helvetica", _configStore.LastSaved!.UiFontFamily);
    }
}
