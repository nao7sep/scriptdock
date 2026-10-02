using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ScriptDock.Models;
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

            // If an unreadable store cannot be set aside, stop before defaults can overwrite it.
            MainWindowViewModel viewModel;
            try
            {
                viewModel = CreateMainViewModel();
            }
            catch (Exception ex)
            {
                Log.Error("startup: a settings file could not be read or set aside", ex);
                desktop.MainWindow = NoticeDialog.CreateStartupFailure(
                    I18n.Message.Of("startup.failedTitle"),
                    FailurePresentation.StartupData());
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
            };
            mainWindow.RestoreWindowGeometry();
            desktop.MainWindow = mainWindow;
            _mainWindow = mainWindow;
            RegisterOwnerActivation(mainWindow);

            // Report material recovery once the main window can own the dialog.
            mainWindow.Opened += async (_, _) =>
            {
                var quarantined = Storage.QuarantineJournal.Drain();
                if (quarantined.Count > 0)
                {
                    await Views.NoticeDialog.ShowAsync(
                        mainWindow,
                        I18n.Message.Of("startup.settingsResetTitle"),
                        FailurePresentation.RecoveredData());
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void RegisterOwnerActivation(Window window)
    {
        if (!OperatingSystem.IsWindows())
            return;

        SingleInstanceLease.RegisterOwnerActivationHandler(() => Dispatcher.UIThread.Post(() =>
        {
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;
            if (!window.IsVisible)
                window.Show();
            window.Activate();
        }));
    }

    /// <summary>
    /// Composition root: builds persistence and the view model by hand (no DI
    /// container). Durable preferences live in <c>config.json</c>, volatile session
    /// state in <c>state.json</c>, the last scan's paths in <c>known-paths.json</c>, what
    /// happened in <c>records.sqlite3</c>. Absent config sets use live built-ins without writing.
    /// </summary>
    private static MainWindowViewModel CreateMainViewModel()
    {
        var configStore = new ConfigStore();
        // not recorded: volatile presentation and process residue, harmless to lose.
        var stateStore = new JsonStore<AppState>(AppPaths.StateFileName, "state", recordBackups: false);
        // not recorded: rebuildable, the last scan's result.
        var knownPathsStore = new JsonStore<KnownPaths>(AppPaths.KnownPathsFileName, "known paths", recordBackups: false);

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
        return new MainWindowViewModel(configStore, stateStore, knownPathsStore, records, config, state, knownPaths, scanner, runner)
        {
            ComputerLanguages = ComputerLanguages,
        };
    }
}
