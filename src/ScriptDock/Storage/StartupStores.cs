using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using ScriptDock.Models;
using ScriptDock.Services;

namespace ScriptDock.Storage;

/// <summary>The required startup reads, performed before the dispatcher starts. The same config
/// supplies native language bootstrap and the window; failures are presented by the startup gate.</summary>
internal sealed class StartupStores
{
    public IConfigStore ConfigStore { get; }
    public IJsonStore<HiddenScripts> HiddenStore { get; }
    public IJsonStore<AppState> StateStore { get; }
    public IJsonStore<KnownPaths> KnownPathsStore { get; }
    public AppConfig Config { get; private set; } = new();
    public HiddenScripts Hidden { get; private set; } = new();
    public AppState State { get; private set; } = new();
    public KnownPaths KnownPaths { get; private set; } = new();
    private ExceptionDispatchInfo? _failure;

    internal StartupStores(IConfigStore config, IJsonStore<HiddenScripts> hidden, IJsonStore<AppState> state, IJsonStore<KnownPaths> paths)
    {
        ConfigStore = config;
        HiddenStore = hidden;
        StateStore = state;
        KnownPathsStore = paths;
        try
        {
            Config = config.Load();
            Hidden = hidden.Load();
            State = state.Load();
            KnownPaths = paths.Load();
        }
        catch (Exception ex) when (ex is NewerFormatVersionException or StoreReadFailedException or QuarantineFailedException)
        {
            _failure = ExceptionDispatchInfo.Capture(ex);
            return;
        }
        if (config.MovedHidden is { } moved)
            MoveHidden(moved);
    }

    // A hidden list config.json held from before hidden.json joins the one there, and leaves config.json
    // once hidden.json holds it. Joining rather than replacing makes the move safe to repeat: one cut off
    // between its two writes leaves the list in both files, and the next launch finishes it. A write that
    // fails leaves the list in config.json, whose saves keep it, so the move is tried again next launch.
    private void MoveHidden(IReadOnlyList<string> moved)
    {
        var added = moved.Distinct(StringComparer.Ordinal)
            .Where(path => !Hidden.Paths.Contains(path, StringComparer.Ordinal)).ToList();
        try
        {
            if (added.Count > 0)
            {
                Hidden.Paths.AddRange(added);
                HiddenStore.SaveAsync(Hidden).GetAwaiter().GetResult();
            }
            ConfigStore.SaveAsync(Config, [Storage.ConfigStore.MovedHiddenKey]).GetAwaiter().GetResult();
            Log.Info("startup: moved the hidden list out of config.json", new { count = moved.Count, added = added.Count });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("startup: the hidden list could not move out of config.json; it moves at a later launch", ex);
        }
    }

    public static StartupStores Load() => new(new ConfigStore(), AppStores.Hidden(), AppStores.State(), AppStores.KnownPaths());
    public void ThrowIfFailed() => _failure?.Throw();
}
