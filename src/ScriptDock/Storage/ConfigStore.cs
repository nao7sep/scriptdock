using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ScriptDock.Models;
using ScriptDock.Services;

namespace ScriptDock.Storage;

/// <summary>Sparse config sets, persisted through the existing atomic managed-text store.</summary>
public sealed class ConfigStore : IConfigStore
{
    private readonly JsonStore<Dictionary<string, JsonElement>> _store = new(AppPaths.ConfigFileName, "config");
    private readonly HashSet<string> _warnedKeys = [];
    private readonly object _queueGate = new();
    private Task _queueTail = Task.CompletedTask;

    public AppConfig Load()
    {
        var config = new AppConfig();
        foreach (var (key, value) in _store.Load())
        {
            if (!ConfigSets.IsKnown(key))
                continue;
            config.StoredSetKeys.Add(key);
            try
            {
                ConfigSets.Apply(config, key, value);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                if (_warnedKeys.Add(key))
                    Log.Warn("config: invalid set, using built-in", ex, new { key });
            }
        }
        return config;
    }

    public Task SaveSetsAsync(AppConfig value, IReadOnlyCollection<string> keys, IReadOnlyCollection<string>? resetKeys = null)
    {
        // Snapshot the whole changed sets before any await or later mutation of the live config.
        var resets = resetKeys?.ToArray() ?? [];
        var replacements = keys.Except(resets).ToDictionary(key => key, key => ConfigSets.Value(value, key));
        if (replacements.Count == 0 && resets.Length == 0)
            return Task.CompletedTask;
        lock (_queueGate)
        {
            _queueTail = ContinueQueue(_queueTail, replacements, resets);
            return _queueTail;
        }
    }

    private async Task ContinueQueue(Task previous, Dictionary<string, JsonElement> replacements, string[] resets)
    {
        try { await previous.ConfigureAwait(false); }
        catch { /* The caller of the earlier write observes its failure. */ }

        await Task.Run(async () =>
        {
            var map = _store.Load();
            foreach (var key in map.Keys.Where(key => !ConfigSets.IsKnown(key)).ToArray())
                map.Remove(key);
            foreach (var key in resets)
                map.Remove(key);
            foreach (var (key, value) in replacements)
                map[key] = value;
            await _store.SaveAsync(map).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }
}
