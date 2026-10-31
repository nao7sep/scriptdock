using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ScriptDock.Models;
using ScriptDock.Storage;
using ScriptDock.Tests.Fakes;
using Xunit;

namespace ScriptDock.Tests.Storage;

public sealed class StartupStoresTests
{
    private sealed class Config : IConfigStore
    {
        public int Reads;
        public AppConfig Value = new() { Language = "ja" };
        public Exception? Failure;
        public IReadOnlyList<string> KeptKeys => [];
        public AppConfig Load() { Reads++; if (Failure is not null) throw Failure; return Value; }
        public Task SaveAsync(AppConfig value, IReadOnlyCollection<string>? replaced = null) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Startup_retains_the_same_loaded_config_for_language_and_window_without_rereading()
    {
        var config = new Config();
        var hidden = new FakeJsonStore<HiddenScripts>();
        var state = new FakeJsonStore<AppState>();
        var paths = new FakeJsonStore<KnownPaths>();
        var loaded = await Task.Run(() => new StartupStores(config, hidden, state, paths), TestContext.Current.CancellationToken);
        config.Value = new AppConfig { Language = "en" };
        loaded.ThrowIfFailed();
        Assert.Equal("ja", loaded.Config.Language);
        Assert.Same(hidden.Value, loaded.Hidden);
        Assert.Same(state.Value, loaded.State);
        Assert.Same(paths.Value, loaded.KnownPaths);
        Assert.Same(config, loaded.ConfigStore);
        loaded.ThrowIfFailed();
        Assert.Equal(1, config.Reads);
        Assert.Equal(1, hidden.LoadCount);
        Assert.Equal(1, state.LoadCount);
        Assert.Equal(1, paths.LoadCount);
    }

    [Fact]
    public void A_protected_read_failure_survives_until_the_startup_gate_and_never_becomes_defaults()
    {
        var failure = new StoreReadFailedException("config.json", new UnauthorizedAccessException());
        var config = new Config { Failure = failure };
        var hidden = new FakeJsonStore<HiddenScripts>();
        var state = new FakeJsonStore<AppState>();
        var paths = new FakeJsonStore<KnownPaths>();
        var loaded = new StartupStores(config, hidden, state, paths);
        Assert.Same(failure, Assert.Throws<StoreReadFailedException>(loaded.ThrowIfFailed));
        Assert.Equal(1, config.Reads);
        Assert.Equal(0, hidden.LoadCount);
        Assert.Equal(0, state.LoadCount);
        Assert.Equal(0, paths.LoadCount);
    }
}
