using System;
using System.Collections.Generic;
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
        // Start in the computer language for storage failures; the one required config load below
        // supplies the saved language before Avalonia initializes native UI.
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
            App.StartupFailure = [FailurePresentation.StartupStorage()];
            // Diagnostics on stderr, which stay English with the exception's own message: this is the
            // log channel, not an interface surface. What the reader sees is the notice window above.
            Console.Error.WriteLine("ScriptDock cannot start: its storage location could not be created. " + ex.Message);
            _ = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 1;
        }

        // Every file the launch opens is checked before anything is written (store-recovery-conventions):
        // the JSON stores are read, and the two databases' headers are read without opening them for writing.
        // Any refused file stops the launch, and one notice names them all. Until the records open, the
        // stores' log lines go to the console.
        var stores = System.Threading.Tasks.Task.Run(StartupStores.Load).GetAwaiter().GetResult();
        Localizer.Use(stores.Config.Language, App.ComputerLanguages);
        var refused = new List<RefusedFile>(stores.Refused);
        if (FileHeader.ProbeNewer(System.IO.Path.Combine(StorageRoot.Directory, RecordStore.FileName), FileHeader.Kinds.Records) is { } newerRecords)
            refused.Add(newerRecords);
        if (FileHeader.ProbeNewer(BackupStore.StoreFile, FileHeader.Kinds.Backups) is { } newerBackups)
            refused.Add(newerBackups);
        if (refused.Count > 0)
            return StopLaunch(args, refused);

        // The records hold this session's log; the logger installs its own crash hooks and
        // console fallback. The session is this launch, by its start time. Records a newer version
        // wrote that the check above could not judge are still refused before anything writes to them.
        RecordStore records;
        try
        {
            records = new RecordStore(StorageRoot.Directory, DateTimeOffset.UtcNow);
        }
        catch (NewerFormatVersionException ex)
        {
            return StopLaunch(args, [new RefusedFile(ex.FilePath, Newer: true)]);
        }
        Log.Start(records);
        App.Records = records;
        // One session id for the launch, so its backup rows and run records match.
        BackupStore.Session = records.Session;
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
            App.PreparedStores = stores;
            // An existing settings history that cannot be opened is reported once the window shows.
            App.BackupsUnavailable = !BackupStore.OpenExisting();
            LanguageBootstrap.AlignAppKit(Localizer.Language);
            // On macOS a logout or shutdown ends the process as soon as the lifetime exits, before the
            // finally below runs, so what the quit logged is written here first, within the exit deadline.
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(
                args,
                lifetime => lifetime.Exit += (_, _) => records.Flush(Exit().Remaining));
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
            // The owners' shutdown draws on what remains of the quit's one exit deadline. When the system
            // ends the session, the backup history is not waited for.
            var exit = Exit();
            BackupStore.Shutdown(exit.SessionEnding ? TimeSpan.Zero : exit.Remaining);
            Log.Info("shutdown", new { clean });
            Log.Shutdown();
            records.Close(exit.Remaining);
        }
    }

    // Shows the one notice naming each refused file and ends the launch. Nothing has been written.
    private static int StopLaunch(string[] args, IReadOnlyList<RefusedFile> refused)
    {
        foreach (var file in refused)
            Console.Error.WriteLine($"ScriptDock cannot start: {file.Path} {(file.Newer ? "was saved by a newer version" : "could not be read")}.");
        App.StartupFailure = FailurePresentation.LaunchStopped(refused);
        LanguageBootstrap.AlignAppKit(Localizer.Language);
        _ = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 1;
    }

    // The deadline the quit fixed, or, when the app ends without one (a startup failure), one fixed now.
    private static ExitDeadline? _unquitExit;
    private static ExitDeadline Exit() =>
        App.QuitViewModel?.Exit ?? (_unquitExit ??= new ExitDeadline(TimeProvider.System));

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
