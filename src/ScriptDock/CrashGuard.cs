using Avalonia.Threading;
using ScriptDock.Services;

namespace ScriptDock;

/// <summary>
/// Keeps the window alive when a UI-thread delegate throws. ScriptDock owns the user's
/// running scripts as child processes, so its own crash is uniquely costly — it would
/// orphan or break the pipes of every running script. A logic bug in a command or event
/// handler must therefore degrade to a logged error, never tear the process down.
/// </summary>
/// <remarks>
/// This is the backstop, not the first line: every event handler, command, and background
/// callback wraps its own work in try/catch (Layer 1). This guard catches anything those
/// miss. It cannot stop a hard kill (SIGKILL, OOM) — that is what the runner's file-backed
/// output (Layer 2) defends against, by leaving the children no pipe to break.
/// </remarks>
public static class CrashGuard
{
    private static bool _installed;

    public static void Install()
    {
        if (_installed)
            return;
        _installed = true;

        // Ensure a dispatched-delegate exception is routed to UnhandledException rather
        // than rethrown past the loop.
        Dispatcher.UIThread.UnhandledExceptionFilter += (_, e) => e.RequestCatch = true;

        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log.Error("ui: unhandled exception caught by the crash guard; window kept alive", e.Exception);
            e.Handled = true;
        };
    }
}
