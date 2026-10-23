using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace ScriptDock.Services;

/// <summary>Registers only explicitly launched instances, so launchers never infer process ownership
/// from a command line. This proves process startup, not readiness of the interface.</summary>
internal static class LauncherRuntime
{
    internal static void Register(string[] args, bool ownsInstance)
    {
        string? registration = null;
        string? token = null;
        for (var index = 0; index + 1 < args.Length; index++)
        {
            if (args[index] == "--launcher-registration") registration = args[++index];
            else if (args[index] == "--launcher-token") token = args[++index];
        }
        if (registration is null || string.IsNullOrEmpty(token) || token.Contains('\n') || token.Contains('\r')
            || !Path.IsPathFullyQualified(registration))
            return;

        string? temporary = null;
        try
        {
            var owner = Path.Combine(Path.GetDirectoryName(registration)!, "launcher-runtime-owner");
            if (File.ReadAllText(owner).TrimEnd('\r', '\n') != token)
                return;
            using var process = Process.GetCurrentProcess();
            var pid = ownsInstance ? process.Id : 0;
            var started = new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeSeconds();
            var payload = token + "\n" + pid.ToString(CultureInfo.InvariantCulture) + "\n"
                + started.ToString(CultureInfo.InvariantCulture) + "\n";
            temporary = registration + "." + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, payload, new UTF8Encoding(false));
            if (File.ReadAllText(owner).TrimEnd('\r', '\n') == token)
                File.Move(temporary, registration, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // Registration is optional app startup bookkeeping. Its launcher will fail its bounded
            // startup wait when no acknowledgement is published, while ordinary app startup continues.
            Console.Error.WriteLine("ScriptDock launcher registration failed: " + ex);
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine("ScriptDock launcher registration cleanup failed: " + ex);
                }
            }
        }
    }
}
