using System;
using System.Diagnostics;

namespace ScriptDock.Services;

/// <summary>
/// Edge helper for opening an external URL in the user's default browser via the OS shell
/// handler. It logs full diagnostics and returns whether the request was accepted so the
/// initiating UI surface can independently present a concise recovery result.
/// </summary>
public static class ExternalLauncher
{
    public static bool Open(string url) => Open(url, Process.Start);

    internal static bool Open(string url, Func<ProcessStartInfo, Process?> start)
    {
        try
        {
            using var process = start(new ProcessStartInfo(url) { UseShellExecute = true });
            if (process is not null)
                return true;

            Log.Error("open external: no process returned", new { url });
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("open external: failed", ex, new { url });
            return false;
        }
    }
}
