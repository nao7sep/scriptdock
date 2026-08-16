using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.Storage;
using ScriptDock.ViewModels;
using ScriptDock.Views;

namespace ScriptDock;

public partial class App : Application
{
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
            // A store that is unreadable AND cannot be set aside throws out of the composition root:
            // ScriptDock must not reset over bytes it failed to preserve, so it halts. A halt has to
            // reach the user, and before a main window exists the report becomes the main window —
            // a silent exit is not a halt (storage-path conventions).
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
                    "A settings file could not be read, and ScriptDock could not set it aside either — so it has "
                    + "been left exactly where it is rather than risk overwriting it.\n\n"
                    + ex.Message
                    + "\n\nYour scripts are not affected. Repair or move the file under the ScriptDock data "
                    + "folder, then start ScriptDock again.");
                base.OnFrameworkInitializationCompleted();
                return;
            }

            var mainWindow = new MainWindow
            {
                DataContext = viewModel,
            };
            desktop.MainWindow = mainWindow;

            // Report any quarantine the startup loads performed: the store was
            // set aside with its bytes preserved and defaults took over — the
            // user hears it from a dialog, never only from the log
            // (storage-path conventions: both branches report).
            mainWindow.Opened += async (_, _) =>
            {
                var quarantined = Storage.QuarantineJournal.Drain();
                if (quarantined.Count > 0)
                {
                    await Views.NoticeDialog.ShowAsync(
                        mainWindow,
                        "A settings file was reset",
                        "A file was unreadable and has been set aside so nothing is lost:\n\n" +
                        string.Join("\n", quarantined) +
                        "\n\nScriptDock started with defaults for it. Your scripts are untouched.");
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
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
