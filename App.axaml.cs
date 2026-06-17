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
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = CreateMainViewModel(),
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
