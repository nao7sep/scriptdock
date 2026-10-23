using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.Tests.Fakes;
using ScriptDock.ViewModels;
using ScriptDock.Views;
using Xunit;

namespace ScriptDock.Tests.Views;

/// <summary>
/// Cmd/Ctrl+W closes the window it is pressed in, the way that window's close button does, and never another
/// window (keyboard-shortcut-conventions, Closing a window). These press the real chord on each kind of
/// window the app opens; on macOS the Window menu's Close takes the chord first and sends AppKit's own close.
/// </summary>
public sealed class CloseWindowShortcutTests : WindowTest
{
    private static void PressClose(Window window)
    {
        var command = ShortcutCatalog.CommandModifier(window) == KeyModifiers.Meta ? RawInputModifiers.Meta : RawInputModifiers.Control;
        // The press alone closes; no release follows, since the window may finish closing at any moment
        // after it and a closed window takes no input.
        window.KeyPress(Key.W, command, PhysicalKey.W, "w");
        Dispatcher.UIThread.RunJobs();
    }

    private static bool SettleUntil(Func<bool> done) =>
        System.Threading.SpinWait.SpinUntil(() => { Dispatcher.UIThread.RunJobs(); return done(); }, TimeSpan.FromSeconds(5));

    private static MainWindowViewModel NewMainVm(FakeProcessRunner runner)
    {
        var config = new AppConfig();
        var state = new AppState();
        return new MainWindowViewModel(
            new FakeConfigStore { Value = config }, new FakeJsonStore<AppState> { Value = state }, new FakeJsonStore<KnownPaths>(),
            new FakeRecordStore(), config, state, new KnownPaths(), new ScriptScanner(), runner);
    }

    [AvaloniaFact]
    public void It_closes_the_records_window_and_hands_the_keyboard_back_to_the_main_window()
    {
        var stateStore = new FakeJsonStore<AppState>();
        var host = new RecordsWindowHost(() => new RecordsWindowViewModel(new FakeRecordReader(), stateStore, stateStore.Value, new FakeTimeProvider()));
        var main = Show(new MainWindow { DataContext = NewMainVm(new FakeProcessRunner()), Records = host });
        var records = Track(host.ShowOrActivate(main));
        Dispatcher.UIThread.RunJobs();
        Assert.True(records.IsActive);

        PressClose(records);

        Assert.False(records.IsVisible);
        Assert.Null(host.Window);
        Assert.True(main.IsVisible);
        Assert.True(main.IsActive);
    }

    [AvaloniaFact]
    public void It_closes_a_dialog_and_not_the_window_under_it()
    {
        var owner = Show(new Window { Width = 400, Height = 300 });
        var dialog = Track(new ConfirmDialog(ScriptDock.I18n.Message.Of("quit.title"), ScriptDock.I18n.Message.Of("quit.title"), "quit.confirm"));
        var answer = dialog.ShowDialog<string?>(owner);
        Dispatcher.UIThread.RunJobs();

        PressClose(dialog);

        Assert.True(answer.IsCompleted);
        Assert.False(dialog.IsVisible);
        Assert.True(owner.IsVisible);
    }

    [AvaloniaFact]
    public async Task On_the_main_window_it_runs_the_quit_flow_which_asks_while_scripts_run()
    {
        var runner = new FakeProcessRunner();
        runner.AddRunning("/x/a.command");
        var vm = NewMainVm(runner);
        var main = Show(new MainWindow { DataContext = vm });
        try
        {
            Assert.True(SettleUntil(vm.ShouldConfirmQuit));

            PressClose(main);

            Assert.True(main.IsVisible);
            var question = Track(Assert.Single(main.OwnedWindows));
            Assert.Equal(0, runner.StopAllCalls);
            question.Close(); // declining keeps the app open
            Dispatcher.UIThread.RunJobs();
            Assert.True(main.IsVisible);
        }
        finally
        {
            // The fake records StopAll without changing its process states. Settle the view model
            // and detach it before fixture Close can open another confirmation after the test ends.
            await vm.ShutdownAsync();
            main.DataContext = null;
            main.Close();
            Assert.True(SettleUntil(() => !main.IsVisible));
            Assert.Empty(main.OwnedWindows);
        }
    }

    [AvaloniaFact]
    public void On_the_main_window_with_nothing_running_it_closes_after_the_quits_saves()
    {
        var runner = new FakeProcessRunner();
        var main = Show(new MainWindow { DataContext = NewMainVm(runner) });

        PressClose(main);

        Assert.True(SettleUntil(() => !main.IsVisible));
        Assert.Equal(1, runner.StopAllCalls);
    }

    [AvaloniaFact]
    public void Another_chord_with_W_does_nothing()
    {
        var owner = Show(new Window());
        var records = Show(new RecordsWindow { DataContext = new RecordsWindowViewModel(new FakeRecordReader(), new FakeJsonStore<AppState>(), new AppState(), new FakeTimeProvider()) });

        records.KeyPress(Key.W, RawInputModifiers.None, PhysicalKey.W, "w");
        records.KeyPress(Key.W, RawInputModifiers.Shift | (ShortcutCatalog.CommandModifier(records) == KeyModifiers.Meta ? RawInputModifiers.Meta : RawInputModifiers.Control), PhysicalKey.W, "W");
        Dispatcher.UIThread.RunJobs();

        Assert.True(records.IsVisible);
        Assert.True(owner.IsVisible);
    }

    [AvaloniaFact]
    public void The_shortcuts_dialog_lists_it()
    {
        var items = ShortcutCatalog.Build(Show(new Window()));
        var close = Assert.Single(items, item => item.DescriptionKey == "shortcuts.closeWindow");
        Assert.EndsWith("+W", close.Label);
        Assert.Null(close.Gesture); // each window binds it, not the main window's accelerators
    }
}
