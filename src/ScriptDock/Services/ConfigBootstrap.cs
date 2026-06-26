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
            return store.Load();

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
