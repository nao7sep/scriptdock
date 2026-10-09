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
/// later save writes it back unchanged, until the user saves a new value for that set. The two process
/// settings v0.1.0 stored were retired deliberately, so they are dropped instead.
/// </remarks>
public sealed class ConfigStore : IConfigStore
{
    private static readonly HashSet<string> RetiredKeys = ["killProcessesOnClose", "recaptureProcessesOnLaunch"];

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
            if (RetiredKeys.Contains(key))
                continue;
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

    public Task SaveAsync(AppConfig value)
    {
        var document = ConfigSets.Changed(value);
        // A set the user has now given a value of its own replaces the kept one for good.
        foreach (var key in _kept.Keys.Where(document.ContainsKey).ToList())
            _kept.Remove(key);
        foreach (var (key, kept) in _kept)
            document[key] = kept;
        return _store.SaveAsync(document);
    }
}
