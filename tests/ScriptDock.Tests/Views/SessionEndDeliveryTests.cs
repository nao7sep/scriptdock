using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.Storage;
using ScriptDock.Tests.Fakes;
using ScriptDock.ViewModels;
using ScriptDock.Views;
using Xunit;

namespace ScriptDock.Tests.Views;

/// <summary>
/// A close the operating system starts, delivered into the real main window's close handler the way the
/// platform lifetime delivers it (unsaved-edits-conventions, Quitting): a session end never asks, never
/// cancels the close, and runs the quit's work within its bounds; a quit the user started while scripts run
/// still asks first. No real logout is involved.
/// </summary>
public sealed class SessionEndDeliveryTests : WindowTest, IDisposable
{
    private readonly FakeConfigStore _configStore = new();
    private readonly FakeJsonStore<AppState> _stateStore = new();
    private readonly FakeProcessRunner _runner = new();
    private readonly FakeTimeProvider _clock = new();
    private TaskCompletionSource? _settingsWrite;
    private MainWindow? _window;

    void IDisposable.Dispose()
    {
        _settingsWrite?.TrySetResult();
        // A window a test left asking closes the way the session ends, which never asks again.
        if (_window is { IsVisible: true } window)
            DeliverClose(window, WindowCloseReason.OSShutdown);
        Dispose();
    }

    private async Task<(MainWindow Window, MainWindowViewModel Vm)> ShowWithRunningScriptAsync()
    {
        var vm = new MainWindowViewModel(
            _configStore, _stateStore, new FakeJsonStore<KnownPaths>(), new FakeJsonStore<HiddenScripts>(), new FakeRecordStore(),
            _configStore.Value, _stateStore.Value, new KnownPaths(), new HiddenScripts(), new ScriptScanner(), _runner)
        {
            Time = _clock,
        };
        _runner.AddRunning("/x/a.command");
        var window = _window = Show(new MainWindow { DataContext = vm });
        // The window's first load builds the Recent list, and with it the running count, after an
        // off-thread read.
        await Wait.UntilAsync(vm.ShouldConfirmQuit, vm);
        return (window, vm);
    }

    // The platform lifetime's own entry: Avalonia closes each window with the reason it was given.
    private static void DeliverClose(Window window, WindowCloseReason reason)
    {
        var closeCore = typeof(Window)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(m => m.Name == "CloseCore" && m.GetParameters().FirstOrDefault()?.ParameterType == typeof(WindowCloseReason));
        var arguments = closeCore.GetParameters()
            .Select((p, i) => i == 0 ? reason : (object?)(p.ParameterType == typeof(bool) ? false : p.DefaultValue))
            .ToArray();
        closeCore.Invoke(window, arguments);
    }

    [AvaloniaFact]
    public async Task A_session_end_closes_at_once_without_asking_and_stops_the_scripts()
    {
        var (window, _) = await ShowWithRunningScriptAsync();
        var closed = false;
        window.Closed += (_, _) => closed = true;

        DeliverClose(window, WindowCloseReason.OSShutdown);

        // The handler returned only after the quit's work, and did not cancel the close.
        Assert.True(closed);
        Assert.Empty(window.OwnedWindows); // no quit question
        Assert.Equal(1, _runner.StopAllCalls);
        Assert.Equal(1, _stateStore.SaveCount);
    }

    [AvaloniaFact]
    public async Task A_settings_write_still_running_at_a_session_end_holds_it_only_to_the_exit_deadline()
    {
        var (window, vm) = await ShowWithRunningScriptAsync();
        _settingsWrite = new TaskCompletionSource();
        _configStore.SaveGate = _settingsWrite;
        var hide = vm.ToggleHiddenCommand.ExecuteAsync(new ScriptItem("/x/a.command") { DisplayName = "a" });
        var closed = false;
        window.Closed += (_, _) => closed = true;

        // Runs inside the close's wait, once the settings wait has started on the deadline.
        Dispatcher.UIThread.Post(() => _clock.Advance(ExitDeadline.Length));
        DeliverClose(window, WindowCloseReason.OSShutdown);

        Assert.True(closed);
        Assert.Empty(window.OwnedWindows); // not the Retry / Quit anyway question
        Assert.Equal(1, _runner.StopAllCalls);

        _settingsWrite.SetResult();
        await hide;
    }

    [AvaloniaFact]
    public async Task A_session_end_takes_over_a_quit_question_already_showing()
    {
        var (window, _) = await ShowWithRunningScriptAsync();
        window.Close(); // the user's own close asks first
        Dispatcher.UIThread.RunJobs();
        var question = Assert.Single(window.OwnedWindows);
        Assert.Equal(0, _runner.StopAllCalls);
        var closed = false;
        window.Closed += (_, _) => closed = true;

        DeliverClose(window, WindowCloseReason.OSShutdown);
        Dispatcher.UIThread.RunJobs();

        Assert.True(closed);
        Assert.Equal(1, _runner.StopAllCalls);
    }

    // Windows reports a logoff as an app shutdown, a session end; on macOS an app shutdown whose quit event
    // carries no reason is the Dock's or the menu's Quit, which asks while scripts run.
    [AvaloniaFact]
    public async Task An_app_shutdown_asks_only_where_it_is_the_users_quit()
    {
        var (window, _) = await ShowWithRunningScriptAsync();
        var closed = false;
        window.Closed += (_, _) => closed = true;

        DeliverClose(window, WindowCloseReason.ApplicationShutdown);
        Dispatcher.UIThread.RunJobs();

        if (OperatingSystem.IsWindows())
        {
            Assert.True(closed);
            Assert.Empty(window.OwnedWindows);
            Assert.Equal(1, _runner.StopAllCalls);
        }
        else
        {
            Assert.False(closed);
            Assert.Single(window.OwnedWindows);
            Assert.Equal(0, _runner.StopAllCalls);
        }
    }

    [AvaloniaFact]
    public async Task A_users_close_with_scripts_running_asks_before_stopping_them()
    {
        var (window, _) = await ShowWithRunningScriptAsync();
        var closed = false;
        window.Closed += (_, _) => closed = true;

        DeliverClose(window, WindowCloseReason.WindowClosing);
        Dispatcher.UIThread.RunJobs();

        Assert.False(closed);
        Assert.Single(window.OwnedWindows);
        Assert.Equal(0, _runner.StopAllCalls);
    }

}
