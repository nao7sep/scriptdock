using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.Tests.Fakes;
using Xunit;

namespace ScriptDock.Tests.Services;

/// <summary>
/// The first-run seeding policy: seed defaults exactly when no config file exists, and
/// never re-seed a config the user has deliberately emptied.
/// </summary>
public sealed class ConfigBootstrapTests
{
    [Fact]
    public void FirstRun_SeedsDefaultsAndPersists()
    {
        var store = new FakeJsonStore<AppConfig> { Exists = false };

        var config = ConfigBootstrap.LoadOrSeed(store);

        Assert.Equal(1, store.SaveCount);
        Assert.Same(config, store.LastSaved);
        Assert.Contains(ConfigDefaults.DefaultExtension, config.Extensions);
        Assert.Equal(ConfigDefaults.BuiltInIgnorePatterns, config.IgnorePatterns);
        Assert.Empty(config.RootDirs); // no root is seeded — the scan target is personal, set in Settings
    }

    [Fact]
    public void ExistingConfig_IsLoadedNotReseeded()
    {
        // A user who deliberately cleared every extension: empty, but the file exists.
        var store = new FakeJsonStore<AppConfig> { Exists = true, Value = new AppConfig() };

        var config = ConfigBootstrap.LoadOrSeed(store);

        Assert.Equal(0, store.SaveCount);
        Assert.Empty(config.Extensions);
        Assert.Empty(config.IgnorePatterns);
    }

    [Fact]
    public void DefaultExtension_MatchesPlatform()
    {
        var expected = System.OperatingSystem.IsWindows() ? ".ps1" : ".command";

        Assert.Equal(expected, ConfigDefaults.DefaultExtension);
    }
}
