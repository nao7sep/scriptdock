using Avalonia.Controls;

namespace ScriptDock.Views;

/// <summary>When a quit the user started asks before stopping running scripts.</summary>
public static class MainWindowCloseGuard
{
    /// <summary>A close the user started asks while scripts run: the window's own close, the app menu's Quit,
    /// or on macOS the app shutdown a Dock Quit starts. A session end is decided before this and never
    /// reaches it.</summary>
    public static bool ShouldConfirmQuit(WindowCloseReason reason, bool hasRunningWorkToKill) =>
        (reason is WindowCloseReason.WindowClosing or WindowCloseReason.ApplicationShutdown) && hasRunningWorkToKill;
}
