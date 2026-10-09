using System;
using System.Diagnostics;
using System.IO;

namespace ScriptDock.Tests.Fakes;

internal static class WindowsJunction
{
    // A directory junction needs neither elevation nor Windows Developer Mode.
    public static void Create(string alias, string target)
    {
        var info = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            Arguments = $"/d /c mklink /J \"{alias}\" \"{target}\"",
        };
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start mklink.");
        try
        {
            if (!process.WaitForExit(20_000))
                throw new TimeoutException("mklink did not finish.");
            if (process.ExitCode != 0 || !Directory.Exists(alias))
                throw new IOException($"mklink failed with exit code {process.ExitCode}.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
        }
    }
}
