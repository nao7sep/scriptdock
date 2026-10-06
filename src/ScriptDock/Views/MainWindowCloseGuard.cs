using Avalonia.Controls;

namespace ScriptDock.Views;

/// <summary>What <c>OnClosing</c> should do with the current close request.</summary>
public enum MainWindowCloseAction
{
    /// <summary>Cancel this close and ask the user before killing running work.</summary>
    PromptToConfirmQuit,

    /// <summary>The pane-size/shutdown saves already landed on an earlier pass; let this
    /// re-triggered <c>Close()</c> proceed for real.</summary>
    ProceedToRealClose,

    /// <summary>A save pass from an earlier close request on this window is still in flight;
    /// cancel this one and do nothing further.</summary>
    Drop,

    /// <summary>Cancel this attempt and run the pane-size/shutdown save pass.</summary>
    RunShutdownSavePass,

    /// <summary>The operating system is ending the session and waits for this close: run the quit's
    /// work here, ask nothing, and let the close proceed.</summary>
    EndSession,
}

/// <summary>What the app's own Quit command (the menu item and its shortcut) should do.</summary>
public enum AppQuitAction
{
    /// <summary>Start the app shutdown, which never asks.</summary>
    Quit,

    /// <summary>Ask first, and start the app shutdown only on a yes.</summary>
    PromptThenQuit,

    /// <summary>A quit prompt is already up and decides; do nothing.</summary>
    Drop,
}

/// <summary>
/// The close-sequencing decision for <see cref="MainWindow"/>: a quit the user started (the window's
/// close, the app's own Quit, the Dock's Quit) may prompt; the operating system ending the session
/// never does (<see cref="SessionEnd"/>). Once past the prompt, the pane-size and shutdown
/// saves must run exactly once — a second close request that arrives while they are still in
/// flight (a second Cmd+Q, Alt+F4, or title-bar close click during a slow disk) is dropped rather
/// than re-entering the save pass and running <c>ShutdownAsync</c> a second time concurrently.
/// Kept as a pure function over the window's close state so the sequencing can be tested without
/// a UI thread.
/// </summary>
public static class MainWindowCloseGuard
{
    /// <summary>A close the user started asks while scripts run: the window's own close, or on macOS the app
    /// shutdown a Dock Quit starts. A session end is decided before this and never reaches it.</summary>
    public static bool ShouldConfirmQuit(WindowCloseReason reason, bool hasRunningWorkToKill) =>
        (reason is WindowCloseReason.WindowClosing or WindowCloseReason.ApplicationShutdown) && hasRunningWorkToKill;

    /// <summary>The app's own Quit is a user close (modal-dialog-conventions), so it asks exactly when the
    /// window's close button would; the app shutdown it then starts is not asked about again.</summary>
    public static AppQuitAction DecideAppQuit(bool quitPromptOpen, bool quitConfirmed, bool hasRunningWorkToKill)
    {
        if (quitPromptOpen)
            return AppQuitAction.Drop;
        return !quitConfirmed && ShouldConfirmQuit(WindowCloseReason.WindowClosing, hasRunningWorkToKill)
            ? AppQuitAction.PromptThenQuit
            : AppQuitAction.Quit;
    }

    public static MainWindowCloseAction DecideAction(
        WindowCloseReason reason,
        bool sessionEnd,
        bool quitConfirmed,
        bool hasRunningWorkToKill,
        bool shutdownSaved,
        bool shutdownSaveInProgress)
    {
        // Once a save pass has started or landed, that already-running pass owns the close: check it
        // before the confirm-quit prompt, or a second close request that arrives after new work
        // starts mid-save (the window stays open and interactive during the await) would re-prompt
        // instead of being dropped — and any work that starts after ShutdownAsync's kill pass already
        // ran would never be captured, leaking a running child process past app exit.
        if (shutdownSaved)
            return MainWindowCloseAction.ProceedToRealClose;
        // The system waits for a session end, so it takes over a quit still asking or running.
        if (sessionEnd)
            return MainWindowCloseAction.EndSession;
        if (shutdownSaveInProgress)
            return MainWindowCloseAction.Drop;
        if (!quitConfirmed && ShouldConfirmQuit(reason, hasRunningWorkToKill))
            return MainWindowCloseAction.PromptToConfirmQuit;
        return MainWindowCloseAction.RunShutdownSavePass;
    }
}
