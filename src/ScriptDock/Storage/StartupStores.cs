using System;
using System.Runtime.ExceptionServices;
using ScriptDock.Models;

namespace ScriptDock.Storage;

/// <summary>The required startup reads, performed before the dispatcher starts. The same config
/// supplies native language bootstrap and the window; failures are presented by the startup gate.</summary>
internal sealed class StartupStores
{
    public IConfigStore ConfigStore { get; }
    public IJsonStore<AppState> StateStore { get; }
    public IJsonStore<KnownPaths> KnownPathsStore { get; }
    public AppConfig Config { get; private set; } = new();
    public AppState State { get; private set; } = new();
    public KnownPaths KnownPaths { get; private set; } = new();
    private ExceptionDispatchInfo? _failure;

    internal StartupStores(IConfigStore config, IJsonStore<AppState> state, IJsonStore<KnownPaths> paths)
    {
        ConfigStore = config;
        StateStore = state;
        KnownPathsStore = paths;
        try
        {
            Config = config.Load();
            State = state.Load();
            KnownPaths = paths.Load();
        }
        catch (Exception ex) when (ex is NewerFormatVersionException or StoreReadFailedException or QuarantineFailedException)
        {
            _failure = ExceptionDispatchInfo.Capture(ex);
        }
    }

    public static StartupStores Load() => new(new ConfigStore(), AppStores.State(), AppStores.KnownPaths());
    public void ThrowIfFailed() => _failure?.Throw();
}
