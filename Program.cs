using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using ScriptDock.Services;
using ScriptDock.Storage;

namespace ScriptDock;

sealed class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Resolve and create the storage root before anything else reads or writes it.
        // An unusable SCRIPTDOCK_HOME (or an unwritable home) is a startup error we report
        // and STOP on — never a silent fallback that lets the app run unable to persist.
        // This runs before Log.Start (the log directory lives under the root) and before
        // StorageRoot.LogsDirectory is ever evaluated, so a malformed override can never
        // throw uncaught ahead of the try below.
        try
        {
            StorageRoot.EnsureExists();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                "ScriptDock cannot start: its storage location could not be created. " + ex.Message);
            return 1;
        }

        // One JSON-Lines file per launch under the app's logs directory; the logger
        // installs its own crash hooks and console fallback.
        Log.Start(StorageRoot.LogsDirectory);
        var clean = true;
        try
        {
            Log.Info("startup", new
            {
                version = AppVersion(),
                os = RuntimeInformation.OSDescription,
                arch = RuntimeInformation.OSArchitecture,
                storageDir = StorageRoot.Directory,
                debugLogging = Log.DebugEnabled,
            });
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // The "why" of a forced shutdown; the shutdown line below records that it
            // was not clean.
            Log.Error("fatal: terminated unexpectedly", ex);
            clean = false;
            return 1;
        }
        finally
        {
            Log.Info("shutdown", new { clean });
            Log.Shutdown();
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static string AppVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
}
