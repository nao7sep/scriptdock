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
        Assert.Empty(loaded.Refused);
        Assert.Equal("ja", loaded.Config.Language);
        Assert.Same(hidden.Value, loaded.Hidden);
        Assert.Same(state.Value, loaded.State);
        Assert.Same(paths.Value, loaded.KnownPaths);
        Assert.Same(config, loaded.ConfigStore);
        Assert.Equal(1, config.Reads);
        Assert.Equal(1, hidden.LoadCount);
        Assert.Equal(1, state.LoadCount);
        Assert.Equal(1, paths.LoadCount);
    }

    // Every store is read even after one is refused, so the launch stop names each refused file at once,
    // and a refused store never becomes defaults the window could save.
    [Fact]
    public void Every_refused_store_is_named_and_none_stops_the_others_being_read()
    {
        var config = new Config { Failure = new UnreadableStoreException("/x/config.json", new UnauthorizedAccessException()) };
        var hidden = new FakeJsonStore<HiddenScripts>();
        var state = new FakeJsonStore<AppState> { LoadFailure = new NewerFormatVersionException("/x/state.json", 2, 1) };
        var paths = new FakeJsonStore<KnownPaths>();
        var loaded = new StartupStores(config, hidden, state, paths);
        Assert.Equal([new RefusedFile("/x/config.json", Newer: false), new RefusedFile("/x/state.json", Newer: true)], loaded.Refused);
        Assert.Equal(1, config.Reads);
        Assert.Equal(1, hidden.LoadCount);
        Assert.Equal(1, state.LoadCount);
        Assert.Equal(1, paths.LoadCount);
    }
}
