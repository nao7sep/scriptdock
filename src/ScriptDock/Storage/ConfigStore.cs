using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ScriptDock.Models;
using ScriptDock.Services;

namespace ScriptDock.Storage;

/// <summary>Config sets per the config-sets-conventions, persisted through the atomic managed-text store.</summary>
/// <remarks>
/// A stored entry the app cannot apply — a set whose value fails its checks, or a key this version does not
/// know — is not the app's to delete: the user may have authored it, or a newer version may own it. Each
/// later save writes it back unchanged, until the user saves a new value for that set.
/// </remarks>
public sealed class ConfigStore : IConfigStore
{
    private readonly JsonStore<Dictionary<string, JsonElement>> _store = new(AppPaths.ConfigFileName, "config", FormatVersions.Config);

    // The stored entries the last Load could not apply, by key, kept for every later save.
    private readonly Dictionary<string, JsonElement> _kept = [];

    /// <summary>The keys the last <see cref="Load"/> could not apply, which saves keep unchanged.</summary>
    public IReadOnlyList<string> KeptKeys => [.. _kept.Keys];

    /// <summary>The settings file.</summary>
    public static string FilePath => System.IO.Path.Combine(StorageRoot.Directory, AppPaths.ConfigFileName);

    public AppConfig Load()
    {
        _kept.Clear();
        var config = new AppConfig();
        foreach (var (key, value) in _store.Load())
        {
            if (!ConfigSets.IsKnown(key))
            {
                Log.Warn("config: unknown key, kept unchanged", new { key });
                _kept[key] = value.Clone();
                continue;
            }
            try
            {
                ConfigSets.Apply(config, key, value);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                Log.Warn("config: invalid set, using built-in and keeping it unchanged", ex, new { key });
                _kept[key] = value.Clone();
            }
        }
        return config;
    }

    /// <summary>
    /// Saves <paramref name="value"/>, keeping each stored entry the app could not apply unless
    /// <paramref name="replaced"/> names its set. The caller saves one change at a time, each after the one
    /// before it settles, so a kept entry is retired only once the save that replaced it has landed.
    /// </summary>
    public async Task SaveAsync(AppConfig value, IReadOnlyCollection<string>? replaced = null)
    {
        var document = ConfigSets.Changed(value);
        var chosen = new HashSet<string>(replaced ?? [], StringComparer.Ordinal);
        // A set the user saved a value for replaces its kept entry, even when that value is the built-in.
        var retired = _kept.Keys.Where(key => document.ContainsKey(key) || chosen.Contains(key)).ToList();
        foreach (var (key, kept) in _kept)
            if (!chosen.Contains(key))
                document.TryAdd(key, kept);
        await _store.SaveAsync(document);
        foreach (var key in retired)
            _kept.Remove(key);
    }
}
