using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.Tests.Fakes;
using ScriptDock.ViewModels;
using ScriptDock.Views;
using Xunit;

namespace ScriptDock.Tests.Views;

/// <summary>
/// A quit question attaches only to a window that is showing and never brings a hidden or minimized one
/// back; with none showing it stands alone (window-conventions, Background visibility and activation).
/// </summary>
public sealed class QuitQuestionOwnerTests : WindowTest
{
    private static void SettleUntil(Func<bool> done) =>
        Wait.UntilOnUi(() => done());

    [AvaloniaFact]
    public void The_owner_is_the_first_window_showing()
    {
        var main = Show(new Window());
        var records = Show(new Window());

        Assert.Same(main, DialogBase.ShowingOwner(main, records));
        main.WindowState = WindowState.Minimized;
        Assert.Same(records, DialogBase.ShowingOwner(main, records));
        records.Hide();
        Assert.Null(DialogBase.ShowingOwner(main, records));
        Assert.Null(DialogBase.ShowingOwner(main, null));
    }

    [AvaloniaFact]
    public void Quitting_with_the_main_window_minimized_asks_in_a_standalone_dialog_and_leaves_it_minimized()
    {
        var runner = new FakeProcessRunner();
        runner.AddRunning("/x/a.command");
        var config = new AppConfig();
        var state = new AppState();
        var vm = new MainWindowViewModel(
            new FakeConfigStore { Value = config }, new FakeJsonStore<AppState> { Value = state }, new FakeJsonStore<KnownPaths>(),
            new FakeRecordStore(), config, state, new KnownPaths(), new ScriptScanner(), runner);
        var main = Show(new MainWindow { DataContext = vm });
        SettleUntil(vm.ShouldConfirmQuit);
        main.WindowState = WindowState.Minimized;
        Dispatcher.UIThread.RunJobs();
        s_lastConfirm = null;

        main.Close(); // the taskbar's Close, or the Dock's Quit, reaching the minimized window
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(main.OwnedWindows);
        var question = FindOpenConfirm();
        Assert.NotNull(question);
        Track(question!);
        Assert.Null(question!.Owner);
        Assert.Equal(WindowState.Minimized, main.WindowState);

        question.Close(); // declining
        Dispatcher.UIThread.RunJobs();
        Assert.True(main.IsVisible);
        Assert.Equal(WindowState.Minimized, main.WindowState);
        Assert.Equal(0, runner.StopAllCalls);
    }

    // The headless platform has no window list, so the standalone dialog is found as the one ConfirmDialog
    // the quit created.
    private static ConfirmDialog? FindOpenConfirm() => s_lastConfirm;
    private static ConfirmDialog? s_lastConfirm;

    static QuitQuestionOwnerTests() =>
        Avalonia.Controls.Window.WindowOpenedEvent.AddClassHandler<ConfirmDialog>((dialog, _) => s_lastConfirm = dialog);
}
