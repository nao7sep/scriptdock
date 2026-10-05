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
            """{"formatVersion":1,"uiFontFamily":null,"rootDirs":[null,"/ok"],"extensions":null,"ignorePatterns":null,"hidden":null}""");
        File.WriteAllText(Path.Combine(_root, "known-paths.json"), """{"formatVersion":1,"paths":[null,"/a.command"]}""");

        var config = new ConfigStore().Load();
        var knownPaths = AppStores.KnownPaths().Load();

        Assert.Equal("", config.UiFontFamily);
        Assert.Empty(config.RootDirs);
        Assert.NotNull(config.Extensions);
        Assert.NotNull(config.IgnorePatterns);
        Assert.NotNull(config.Hidden);
        Assert.Equal(["/a.command"], knownPaths.Paths);
    }

    [Fact]
    public void Load_PreservesAnExplicitBundledFontName()
    {
        File.WriteAllText(Path.Combine(_root, "config.json"), """{"formatVersion":1,"uiFontFamily":"Inter"}""");

        var config = new ConfigStore().Load();

        Assert.Equal("Inter", config.UiFontFamily);
    }

    [Fact]
    public void Load_KeepsAStoredValueDifferentFromTheDefault()
    {
        File.WriteAllText(Path.Combine(_root, "config.json"), """{"formatVersion":1,"uiFontFamily":"Helvetica Neue"}""");

        var config = new ConfigStore().Load();

        Assert.Equal("Helvetica Neue", config.UiFontFamily);
    }
}
