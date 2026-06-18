using System;
using System.IO;
using ScriptDock.Storage;
using Xunit;

namespace ScriptDock.Tests.Storage;

/// <summary>
/// Storage-root resolution: <c>SCRIPTDOCK_HOME</c> relocates the whole tree when set, the default
/// <c>~/.scriptdock</c> is used when it is not, and a relative override resolves against the home
/// directory (never the working directory) so no path can depend on how the app was launched.
/// </summary>
[Collection(StorageRootEnvironment.CollectionName)]
public sealed class StorageRootTests : IDisposable
{
    private readonly string? _previousHome;

    public StorageRootTests()
    {
        _previousHome = Environment.GetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, _previousHome);
    }

    [Fact]
    public void Root_Defaults_To_DotScriptdock_When_Override_Unset()
    {
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, null);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(home, ".scriptdock"), StorageRoot.Directory);
    }

    [Fact]
    public void Override_Relocates_The_Whole_Root()
    {
        var target = Path.Combine(Path.GetTempPath(), "scriptdock-home-tests-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, target);

        Assert.Equal(Path.GetFullPath(target), Path.GetFullPath(StorageRoot.Directory));
        // The logs subpath is derived from the relocated root.
        Assert.Equal(Path.Combine(StorageRoot.Directory, "logs"), StorageRoot.LogsDirectory);
    }

    [Fact]
    public void Empty_Override_Falls_Back_To_The_Default()
    {
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, "   ");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(home, ".scriptdock"), StorageRoot.Directory);
    }

    [Fact]
    public void Relative_Override_Resolves_Against_Home_Not_Working_Directory()
    {
        var relative = "scriptdock-relative-" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, relative);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.GetFullPath(Path.Combine(home, relative)), StorageRoot.Directory);
        Assert.NotEqual(
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), relative)),
            StorageRoot.Directory);
    }
}
