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
            desktop.MainWindow = new MainWindow
            {
                DataContext = CreateMainViewModel(),
            };

            // Kick off the just-in-case data backup on a background thread — after CreateMainViewModel
            // has materialized config.json — so the window never waits on it and the first-run seed is
            // already on disk to capture.
            BackupService.RunInBackground();
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
