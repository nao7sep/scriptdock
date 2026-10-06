using System;
using System.IO;
using System.Threading.Tasks;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.Tests.Fakes;
using ScriptDock.ViewModels;
using Xunit;

namespace ScriptDock.Tests.ViewModels;

/// <summary>
/// The quit's own steps, each within its bound on a controlled clock (unsaved-edits-conventions,
/// Quitting): view state never holds a quit, a step that stalls ends at its bound, and a settings change
/// the user made is waited for and reported when it did not land.
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

    // Applies a settings change whose save stays running until the test settles the returned gate.
    private TaskCompletionSource ApplyWithSaveRunning(MainWindowViewModel vm, out Task<bool> apply)
    {
        var gate = new TaskCompletionSource();
        _configStore.SaveGate = gate;
        var draft = vm.CreateSettingsDraft();
        draft.UiFontFamily = "Helvetica";
        apply = vm.TryApplySettingsAsync(draft);
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

        var saved = vm.SettingsSavedForQuitAsync();
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

        var saved = vm.SettingsSavedForQuitAsync();
        gate.SetException(new IOException("disk full (test)"));

        Assert.False(await saved);
        Assert.False(await apply);
    }

    [Fact]
    public async Task A_hide_whose_write_fails_during_the_quit_stops_it()
    {
        var gate = new TaskCompletionSource();
        _configStore.SaveGate = gate;
        var vm = NewVm();
        var hide = vm.ToggleHiddenCommand.ExecuteAsync(new ScriptItem("/x/a.command") { DisplayName = "a" });

        var saved = vm.SettingsSavedForQuitAsync();
        gate.SetException(new IOException("disk full (test)"));
        await hide;

        Assert.False(await saved);
        Assert.Empty(_configStore.Value.Hidden);
    }

    [Fact]
    public async Task A_stalled_settings_write_stops_the_quit_at_its_bound()
    {
        var vm = NewVm();
        ApplyWithSaveRunning(vm, out _);

        var saved = vm.SettingsSavedForQuitAsync();
        _clock.Advance(MainWindowViewModel.QuitSettingsWriteBound - Tick);
        Assert.False(saved.IsCompleted);

        _clock.Advance(Tick);
        Assert.False(await saved);
    }

    [Fact]
    public async Task Retry_saves_the_same_change_again()
    {
        var vm = NewVm();
        var gate = ApplyWithSaveRunning(vm, out var apply);
        var saved = vm.SettingsSavedForQuitAsync();
        gate.SetException(new IOException("disk full (test)"));
        Assert.False(await saved);
        Assert.False(await apply);

        _configStore.SaveGate = null;
        Assert.True(await vm.SettingsSavedForQuitAsync(retry: true));

        Assert.Equal("Helvetica", _configStore.LastSaved!.UiFontFamily);
    }

    [Fact]
    public void The_quits_steps_together_stay_inside_the_systems_wait_at_logout()
    {
        // Windows ends an app about five seconds after logoff or shutdown asks it to close; the records'
        // own two-second log flush at exit comes on top of these.
        var total = MainWindowViewModel.QuitSettingsWriteBound
            + MainWindowViewModel.QuitStateSaveBound
            + MainWindowViewModel.QuitStopScriptsBound
            + MainWindowViewModel.QuitOutputImportBound;

        Assert.True(total + TimeSpan.FromSeconds(2) < TimeSpan.FromSeconds(5), $"the quit may take {total}");
    }
}
