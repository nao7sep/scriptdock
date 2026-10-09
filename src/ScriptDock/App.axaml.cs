using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ScriptDock.Services;
using ScriptDock.Storage;
using ScriptDock.ViewModels;
using ScriptDock.Views;

namespace ScriptDock;

public partial class App : Application
{
    internal static I18n.Message? StartupFailureMessage { get; set; }

    /// <summary>
    /// The computer's own languages, in order, as <c>LanguageBootstrap</c> read them before the app was
    /// built. Kept so that System means the same language for the whole session.
    /// </summary>
    internal static System.Collections.Generic.IReadOnlyList<string> ComputerLanguages { get; set; } = [];

    /// <summary>This session's records, opened by <c>Program</c> before the app was built.</summary>
    internal static IRecordStore? Records { get; set; }

    // The main window, which the app menu's About and Settings items open through. Null while a
    // startup failure is shown instead, when those items are disabled.
    private MainWindow? _mainWindow;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        MenuGestureColumn.Install();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Install the UI-thread crash net before the window exists, so even a failure
        // during first load degrades to a log line instead of taking the process down.
        CrashGuard.Install();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The one macOS menu bar, set before any window so every window, a startup failure notice
            // included, shows the same bar. About and Settings are enabled while the main window is in
            // front, so never over one of its dialogs.
            MacMenuBar.Install(
                "ScriptDock",
                showAbout: () => _mainWindow?.ShowAboutFromMenu(),
                showSettings: () => _mainWindow?.ShowSettingsFromMenu(),
                quit: () => _ = QuitFromMenuAsync(desktop),
                canShowAppDialogs: () => _mainWindow is { IsActive: true });

            // An emoji chosen in the macOS picker arrives while the window is in the background; macOS only.
            BackgroundTextInput.Install();

            if (StartupFailureMessage is { } startupFailure)
            {
                desktop.MainWindow = NoticeDialog.CreateStartupFailure(I18n.Message.Of("startup.failedTitle"), startupFailure);
                RegisterOwnerActivation(desktop.MainWindow);
                base.OnFrameworkInitializationCompleted();
                return;
            }

            // Stop before defaults can overwrite a store that a newer version wrote, or an unreadable one
            // that cannot be set aside.
            MainWindowViewModel viewModel;
            Func<RecordsWindowViewModel> recordsViewModel;
            IReadOnlyList<string> keptSettings;
            try
            {
                (viewModel, recordsViewModel, keptSettings) = CreateViewModels();
            }
            catch (NewerFormatVersionException ex)
            {
                // Already logged by the store; the file stays exactly as it is.
                desktop.MainWindow = NoticeDialog.CreateStartupFailure(
                    I18n.Message.Of("startup.failedTitle"),
                    FailurePresentation.NewerStore(ex.FilePath));
                RegisterOwnerActivation(desktop.MainWindow);
                base.OnFrameworkInitializationCompleted();
                return;
            }
            catch (QuarantineFailedException ex)
            {
                Log.Error("startup: a settings file could not be read or set aside", ex);
                desktop.MainWindow = NoticeDialog.CreateStartupFailure(
                    I18n.Message.Of("startup.failedTitle"),
                    FailurePresentation.StartupData(ex.FilePath));
                RegisterOwnerActivation(desktop.MainWindow);
                base.OnFrameworkInitializationCompleted();
                return;
            }

            // Before the main window exists, so its first frame and title bar take the saved theme.
            // The startup-failure notices above never read settings, so they follow the OS.
            AppTheme.Apply(viewModel.Theme);
            var mainWindow = new MainWindow
            {
                DataContext = viewModel,
                Records = new RecordsWindowHost(recordsViewModel),
            };
            mainWindow.RestoreWindowGeometry();
            desktop.MainWindow = mainWindow;
            _mainWindow = mainWindow;
            RegisterOwnerActivation(mainWindow);
            RegisterReopen(mainWindow);

            // Report material recovery once the main window can own the dialog.
            mainWindow.Opened += async (_, _) =>
            {
                foreach (var quarantined in Storage.QuarantineJournal.Drain())
                {
                    await Views.NoticeDialog.ShowAsync(
                        mainWindow,
                        I18n.Message.Of("startup.settingsResetTitle"),
                        FailurePresentation.RecoveredData(quarantined));
                }
                if (keptSettings.Count > 0)
                {
                    await Views.NoticeDialog.ShowAsync(
                        mainWindow,
                        I18n.Message.Of("startup.settingsKeptTitle"),
                        FailurePresentation.KeptSettings(ConfigStore.FilePath, keptSettings));
                }
                if (Records is RecordStore { DatabaseUnavailable: true } records)
                {
                    await Views.NoticeDialog.ShowAsync(
                        mainWindow,
                        I18n.Message.Of("startup.recordsUnavailableTitle"),
                        FailurePresentation.RecordsUnavailable(records.FilePath));
                }
                await mainWindow.AskForFirstScanFolderAsync();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    // The app's own Quit (the menu item and its Cmd+Q) asks first while scripts run, as the main
    // window's close button does; the shutdown it then starts never asks again. With no main window
    // (a startup failure notice) nothing runs, so it quits at once.
    private async System.Threading.Tasks.Task QuitFromMenuAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            if (_mainWindow is null || await _mainWindow.ConfirmAppQuitAsync())
                desktop.TryShutdown();
        }
        catch (Exception ex)
        {
            Log.Error("ui: quit from the menu failed", ex);
        }
    }

    private static void RegisterOwnerActivation(Window window)
    {
        if (!OperatingSystem.IsWindows())
            return;

        SingleInstanceLease.RegisterOwnerActivationHandler(() => Dispatcher.UIThread.Post(() => WindowActivation.BringBack(window)));
    }

    // On macOS a Dock click or a second launch asks the running app to reopen. AppKit restores a window
    // only when none is visible, so while the Records window shows, the main window is brought back here.
    private void RegisterReopen(Window window)
    {
        if (this.TryGetFeature<IActivatableLifetime>() is not { } activatable)
            return;

        activatable.Activated += (_, e) =>
        {
            if (e.Kind == ActivationKind.Reopen)
                WindowActivation.BringBack(window);
        };
    }

    /// <summary>
    /// Composition root: builds persistence and the view models by hand (no DI
    /// container). Durable preferences live in <c>config.json</c>, view state in
    /// <c>state.json</c>, the last scan's paths in <c>known-paths.json</c>, what
    /// happened in <c>records.sqlite3</c>. Absent config sets use live built-ins without writing.
    /// </summary>
    private static (MainWindowViewModel Main, Func<RecordsWindowViewModel> Records, IReadOnlyList<string> KeptSettings) CreateViewModels()
    {
        var configStore = new ConfigStore();
        var stateStore = AppStores.State();
        var knownPathsStore = AppStores.KnownPaths();

        var config = configStore.Load();
        var state = stateStore.Load();
        var knownPaths = knownPathsStore.Load();

        Log.Info("config", new
        {
            rootDirs = config.RootDirs.Count,
            extensions = config.Extensions.Count,
        });

        var scanner = new ScriptScanner();
        var runner = new ProcessRunner();

        var records = Records ?? throw new InvalidOperationException("The records are not open.");
        var main = new MainWindowViewModel(configStore, stateStore, knownPathsStore, records, config, state, knownPaths, scanner, runner)
        {
            ComputerLanguages = ComputerLanguages,
        };
        // The Records window reads the same records and keeps its placement in the same view state.
        var reader = records as IRecordReader ?? throw new InvalidOperationException("The records cannot be read.");
        return (main, () => new RecordsWindowViewModel(reader, stateStore, state), configStore.KeptKeys);
    }
}
