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
