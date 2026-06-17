using ScriptDock.Models;
using ScriptDock.Storage;

namespace ScriptDock.ViewModels;

/// <summary>
/// Root view model. Phase 0 holds the persistence stores and the loaded
/// configuration/state so the composition root and the main window are wired
/// end to end; the script lists, scanning, and the runner arrive in later phases.
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase
{
    private readonly IJsonStore<AppConfig> _configStore;
    private readonly IJsonStore<AppState> _stateStore;
    private readonly AppConfig _config;
    private readonly AppState _state;

    public MainWindowViewModel(
        IJsonStore<AppConfig> configStore,
        IJsonStore<AppState> stateStore,
        AppConfig config,
        AppState state)
    {
        _configStore = configStore;
        _stateStore = stateStore;
        _config = config;
        _state = state;
    }

    /// <summary>Placeholder surface bound by the scaffold window until the real UI lands.</summary>
    public string Placeholder =>
        $"ScriptDock — scaffold ready. {_config.RootDirs.Count} root(s), {_config.Extensions.Count} extension(s) configured.";
}
