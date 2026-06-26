using System;
using System.Diagnostics;

namespace ScriptDock.Services;

/// <summary>
/// Edge helper for opening an external URL in the user's default browser via the OS shell
/// handler. Best-effort and self-contained: a launch failure is caught and logged here
/// rather than bubbling out of a UI click handler as an unhandled exception.
/// </summary>
public static class ExternalLauncher
{
    public static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("open external: failed", ex, new { url });
        }
    }
}
