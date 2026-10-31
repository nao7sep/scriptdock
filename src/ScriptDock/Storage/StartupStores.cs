using System;
using System.Collections.Generic;
using ScriptDock.Models;

namespace ScriptDock.Storage;

/// <summary>The required startup reads, performed before the dispatcher starts and before anything is written.
/// The same config supplies native language bootstrap and the window. Every store is read even after one is
/// refused, so the launch stop names each refused file at once (store-recovery-conventions).</summary>
internal sealed class StartupStores
{
    public IConfigStore ConfigStore { get; }
    public IJsonStore<HiddenScripts> HiddenStore { get; }
    public IJsonStore<AppState> StateStore { get; }
    public IJsonStore<KnownPaths> KnownPathsStore { get; }
    public AppConfig Config { get; } = new();
    public HiddenScripts Hidden { get; } = new();
    public AppState State { get; } = new();
    public KnownPaths KnownPaths { get; } = new();

    /// <summary>The files a newer version wrote or that could not be read, in load order; any stops the launch.</summary>
    public IReadOnlyList<RefusedFile> Refused => _refused;
    private readonly List<RefusedFile> _refused = [];

    internal StartupStores(IConfigStore config, IJsonStore<HiddenScripts> hidden, IJsonStore<AppState> state, IJsonStore<KnownPaths> paths)
    {
        ConfigStore = config;
        HiddenStore = hidden;
        StateStore = state;
        KnownPathsStore = paths;
        Config = Read(config.Load) ?? Config;
        Hidden = Read(hidden.Load) ?? Hidden;
        State = Read(state.Load) ?? State;
        KnownPaths = Read(paths.Load) ?? KnownPaths;
    }

    private T? Read<T>(Func<T> load) where T : class
    {
        try
        {
            return load();
        }
        catch (Exception ex) when (RefusedFile.From(ex) is { } refused)
        {
            _refused.Add(refused);
            return null;
        }
    }

    public static StartupStores Load() => new(new ConfigStore(), AppStores.Hidden(), AppStores.State(), AppStores.KnownPaths());
}
