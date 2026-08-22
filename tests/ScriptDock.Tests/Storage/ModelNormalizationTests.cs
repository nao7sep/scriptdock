using System;
using System.IO;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.Storage;
using Xunit;

namespace ScriptDock.Tests.Storage;

[Collection(StorageRootEnvironment.CollectionName)]
public sealed class ModelNormalizationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scriptdock-tests", NanoId.New());
    private readonly string? _previousHome;

    public ModelNormalizationTests()
    {
        Directory.CreateDirectory(_root);
        _previousHome = Environment.GetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable);
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, _root);
    }

    public void Dispose()
    {
        BackupStore.Close();
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, _previousHome);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Load_NormalizesNestedNullCollectionsAndReferences()
    {
        File.WriteAllText(Path.Combine(_root, "config.json"),
            """{"uiFontFamily":null,"rootDirs":[null,"/ok"],"extensions":null,"ignorePatterns":null,"hidden":null}""");
        File.WriteAllText(Path.Combine(_root, "state.json"),
            """{"knownPaths":null,"recentlyRun":[null,{"path":null}],"runningProcesses":[null,{"pid":0,"scriptPath":null}] }""");

        var config = new JsonStore<AppConfig>("config.json", "config").Load();
        var state = new JsonStore<AppState>("state.json", "state").Load();

        Assert.Equal(AppConfig.DefaultUiFontFamily, config.UiFontFamily);
        Assert.Equal(["/ok"], config.RootDirs);
        Assert.NotNull(config.Extensions);
        Assert.NotNull(config.IgnorePatterns);
        Assert.NotNull(config.Hidden);
        Assert.NotNull(state.KnownPaths);
        Assert.Empty(state.RecentlyRun);
        Assert.Empty(state.RunningProcesses);
    }
}
