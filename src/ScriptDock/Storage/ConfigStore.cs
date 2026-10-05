using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using ScriptDock.Models;
using ScriptDock.Services;

namespace ScriptDock.Storage;

/// <summary>Config sets per the config-sets-conventions, persisted through the atomic managed-text store.</summary>
public sealed class ConfigStore : IConfigStore
{
    private readonly JsonStore<Dictionary<string, JsonElement>> _store = new(AppPaths.ConfigFileName, "config", FormatVersions.Config);

    public AppConfig Load()
    {
        var config = new AppConfig();
        foreach (var (key, value) in _store.Load())
        {
            if (!ConfigSets.IsKnown(key))
                continue;
            try
            {
                ConfigSets.Apply(config, key, value);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                Log.Warn("config: invalid set, using built-in", ex, new { key });
            }
        }
        return config;
    }

    public Task SaveAsync(AppConfig value) => _store.SaveAsync(ConfigSets.Changed(value));
}
