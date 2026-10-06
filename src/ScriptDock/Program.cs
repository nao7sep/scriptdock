using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using ScriptDock.I18n;
using ScriptDock.Services;
using ScriptDock.Storage;
using ScriptDock.Views;

namespace ScriptDock;

sealed class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!SingleInstanceLease.TryAcquire(out var instanceLease))
        {
            Console.Error.WriteLine("ScriptDock is already running.");
            return 0;
        }
        using (instanceLease)
            return Run(args);
    }

    private static int Run(string[] args)
    {
        // The interface language, before anything can draw and before Avalonia creates the macOS
        // application object, which settles the language of the menu items AppKit contributes itself.
        // It reads the saved preference straight from config.json and falls back to the computer's own
        // languages, so it holds on the startup-failure path below, where there is no usable storage.
        App.ComputerLanguages = LanguageBootstrap.Start();

        // Resolve and create the storage root before anything else reads or writes it.
        // An unusable SCRIPTDOCK_DATA_DIR (or an unwritable home) is a startup error we report
        // and STOP on — never a silent fallback that lets the app run unable to persist.
        // This runs before the records open (they live under the root), so a malformed
        // override can never throw uncaught ahead of the try below.
        try
        {
            StorageRoot.EnsureExists();
        }
        catch (Exception ex)
        {
            App.StartupFailureMessage =
                FailurePresentation.StartupStorage();
            // Diagnostics on stderr, which stay English with the exception's own message: this is the
            // log channel, not an interface surface. What the reader sees is the notice window above.
            Console.Error.WriteLine("ScriptDock cannot start: its storage location could not be created. " + ex.Message);
            _ = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 1;
        }

        // The records hold this session's log; the logger installs its own crash hooks and
        // console fallback. The session is this launch, by its start time. Records a newer version
        // wrote are left as they are, and the app stops before anything can write to them.
        RecordStore records;
        try
        {
            records = new RecordStore(StorageRoot.Directory, DateTimeOffset.UtcNow);
        }
        catch (NewerFormatVersionException ex)
        {
            App.StartupFailureMessage = FailurePresentation.NewerStore(ex.FilePath);
            Console.Error.WriteLine("ScriptDock cannot start: " + ex.Message);
            _ = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 1;
        }
        Log.Start(records);
        App.Records = records;
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
            // On macOS a logout or shutdown ends the process as soon as the lifetime exits, before the
            // finally below runs, so what the quit logged is written here first, within the records' bound.
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(
                args,
                lifetime => lifetime.Exit += (_, _) => Log.Flush());
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
            records.Dispose();
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(MacMenuBar.PlatformOptions())
            .WithInterFont()
            .LogToTrace();

    private static string AppVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
}
