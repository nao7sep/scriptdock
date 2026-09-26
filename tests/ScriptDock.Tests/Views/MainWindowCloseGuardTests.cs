using Avalonia.Controls;
using ScriptDock.Views;
using Xunit;

namespace ScriptDock.Tests.Views;

public sealed class MainWindowCloseGuardTests
{
    [Fact]
    public void OnlyDirectWindowCloseMayPrompt()
    {
        Assert.True(MainWindowCloseGuard.ShouldConfirmQuit(WindowCloseReason.WindowClosing, true));
        Assert.False(MainWindowCloseGuard.ShouldConfirmQuit(WindowCloseReason.ApplicationShutdown, true));
        Assert.False(MainWindowCloseGuard.ShouldConfirmQuit(WindowCloseReason.OSShutdown, true));
        Assert.False(MainWindowCloseGuard.ShouldConfirmQuit(WindowCloseReason.OwnerWindowClosing, true));
        Assert.False(MainWindowCloseGuard.ShouldConfirmQuit(WindowCloseReason.WindowClosing, false));
    }

    [Fact]
    public void FirstCloseWithRunningWorkPromptsBeforeSaving()
    {
        var action = MainWindowCloseGuard.DecideAction(
            WindowCloseReason.WindowClosing,
            quitConfirmed: false,
            hasRunningWorkToKill: true,
            shutdownSaved: false,
            shutdownSaveInProgress: false);

        Assert.Equal(MainWindowCloseAction.PromptToConfirmQuit, action);
    }

    [Fact]
    public void FirstCloseWithNoRunningWorkRunsTheSavePass()
    {
        var action = MainWindowCloseGuard.DecideAction(
            WindowCloseReason.WindowClosing,
            quitConfirmed: false,
            hasRunningWorkToKill: false,
            shutdownSaved: false,
            shutdownSaveInProgress: false);

        Assert.Equal(MainWindowCloseAction.RunShutdownSavePass, action);
    }

    [Fact]
    public void SecondCloseWhileTheSavePassIsInFlightIsDropped()
    {
        // This is the reentrancy SD-C1 covers: a second Cmd+Q, Alt+F4, or title-bar close click
        // arriving while PersistPaneSizesAsync/ShutdownAsync from the first close are still
        // running must not start a second, overlapping save-and-shutdown pass.
        var action = MainWindowCloseGuard.DecideAction(
            WindowCloseReason.WindowClosing,
            quitConfirmed: true,
            hasRunningWorkToKill: false,
            shutdownSaved: false,
            shutdownSaveInProgress: true);

        Assert.Equal(MainWindowCloseAction.Drop, action);
    }

    [Fact]
    public void CloseAfterTheSavePassLandedProceedsForReal()
    {
        var action = MainWindowCloseGuard.DecideAction(
            WindowCloseReason.WindowClosing,
            quitConfirmed: true,
            hasRunningWorkToKill: false,
            shutdownSaved: true,
            shutdownSaveInProgress: true);

        Assert.Equal(MainWindowCloseAction.ProceedToRealClose, action);
    }

    [Fact]
    public void OwnerAndAppShutdownNeverPromptEvenWithRunningWork()
    {
        Assert.Equal(
            MainWindowCloseAction.RunShutdownSavePass,
            MainWindowCloseGuard.DecideAction(
                WindowCloseReason.ApplicationShutdown,
                quitConfirmed: false,
                hasRunningWorkToKill: true,
                shutdownSaved: false,
                shutdownSaveInProgress: false));
    }
}
