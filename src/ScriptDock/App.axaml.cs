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
    internal static string? StartupFailureMessage { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Install the UI-thread crash net before the window exists, so even a failure
        // during first load degrades to a log line instead of taking the process down.
        CrashGuard.Install();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (StartupFailureMessage is { } startupFailure)
            {
                desktop.MainWindow = NoticeDialog.CreateStartupFailure("ScriptDock could not start", startupFailure);
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
                    "ScriptDock could not start",
                    FailurePresentation.StartupData());
                RegisterOwnerActivation(desktop.MainWindow);
                base.OnFrameworkInitializationCompleted();
                return;
            }

            var mainWindow = new MainWindow
            {
                DataContext = viewModel,
            };
            desktop.MainWindow = mainWindow;
            RegisterOwnerActivation(mainWindow);

            // Report material recovery once the main window can own the dialog.
            mainWindow.Opened += async (_, _) =>
            {
                var quarantined = Storage.QuarantineJournal.Drain();
                if (quarantined.Count > 0)
                {
                    await Views.NoticeDialog.ShowAsync(
                        mainWindow,
                        "A settings file was reset",
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
    /// state in <c>state.json</c>. On first run <see cref="ConfigBootstrap"/> seeds the
    /// config from <see cref="ConfigDefaults"/> so the window opens against a usable
    /// configuration rather than an empty one.
    /// </summary>
    private static MainWindowViewModel CreateMainViewModel()
    {
        var configStore = new JsonStore<AppConfig>(AppPaths.ConfigFileName, "config");
        var stateStore = new JsonStore<AppState>(AppPaths.StateFileName, "state");

        var config = ConfigBootstrap.LoadOrSeed(configStore);
        var state = stateStore.Load();

        Log.Info("config", new
        {
            rootDirs = config.RootDirs.Count,
            extensions = config.Extensions.Count,
            recentlyRun = state.RecentlyRun.Count,
        });

        var scanner = new ScriptScanner();
        var runner = new ProcessRunner();

        return new MainWindowViewModel(configStore, stateStore, config, state, scanner, runner);
    }
}
