using Avalonia.Controls;

namespace ScriptDock.Views;

/// <summary>Direct window closes may prompt; owner/app/OS shutdown must always drain.</summary>
public static class MainWindowCloseGuard
{
    public static bool ShouldConfirmQuit(WindowCloseReason reason, bool hasRunningWorkToKill) =>
        reason == WindowCloseReason.WindowClosing && hasRunningWorkToKill;
}
