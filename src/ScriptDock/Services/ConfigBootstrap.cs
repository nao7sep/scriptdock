using ScriptDock.Models;
using ScriptDock.Storage;

namespace ScriptDock.Services;

/// <summary>
/// First-run policy for the durable config: if no config file exists yet, seed it from
/// <see cref="ConfigDefaults"/> and persist it; otherwise load what is there. Seeding is
/// keyed on file absence, never on empty lists — a user who clears every extension has
/// made a deliberate choice that must survive a restart.
/// </summary>
public static class ConfigBootstrap
{
    public static AppConfig LoadOrSeed(IJsonStore<AppConfig> store)
    {
        if (store.Exists)
        {
            var loaded = store.Load();
            // Re-check absence AFTER the load: a corrupt file is quarantined away
            // during Load(), and a launch recovered that way must get the seeded
            // defaults below, not an empty config (storage-path conventions — the
            // seeding check runs after the quarantine, not before the load).
            if (store.Exists)
                return loaded;
        }

        var seeded = ConfigDefaults.CreateSeededConfig();
        store.Save(seeded);
        Log.Info("config: seeded first-run defaults", new
        {
            extension = ConfigDefaults.DefaultExtension,
            rootDirs = seeded.RootDirs.Count,
            ignorePatterns = seeded.IgnorePatterns.Count,
        });
        return seeded;
    }
}
