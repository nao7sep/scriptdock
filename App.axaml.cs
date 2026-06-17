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
    /// state in <c>state.json</c>; both are loaded here so the window opens against a
    /// known configuration. Phase 1 fills in the config/state fields and first-run
    /// seeding; the wiring shape is established now.
    /// </summary>
    private static MainWindowViewModel CreateMainViewModel()
    {
        var configStore = new JsonStore<AppConfig>(AppPaths.ConfigFileName, "config");
        var stateStore = new JsonStore<AppState>(AppPaths.StateFileName, "state");

        var config = configStore.Load();
        var state = stateStore.Load();

        Log.Info("config", new { rootDirs = config.RootDirs.Count, extensions = config.Extensions.Count });

        return new MainWindowViewModel(configStore, stateStore, config, state);
    }
}
