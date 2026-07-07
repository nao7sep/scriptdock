using System;
using System.IO;
using ScriptDock;
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
        var target = Path.Combine(Path.GetTempPath(), "scriptdock-home-tests-" + NanoId.New());
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
        var relative = "scriptdock-relative-" + NanoId.New();
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, relative);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.GetFullPath(Path.Combine(home, relative)), StorageRoot.Directory);
        Assert.NotEqual(
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), relative)),
            StorageRoot.Directory);
    }

    [Fact]
    public void Override_Expands_Environment_References()
    {
        // The resolver expands the %VAR% form (here) as well as the POSIX $VAR / ${VAR} forms
        // (covered by the test below); an unset reference expands to empty.
        // Env var *names* must be [A-Za-z0-9_] only (see StorageRoot's EnvReferencePattern), so the
        // nanoid's URL-safe hyphen is remapped to an underscore here — the discriminator only needs
        // to be unique, not to keep every character of the id's usual alphabet.
        var varName = "SCRIPTDOCK_EXPAND_TEST_" + NanoId.New().Replace('-', '_');
        var target = Path.Combine(Path.GetTempPath(), "scriptdock-expand-" + NanoId.New());
        var previousVar = Environment.GetEnvironmentVariable(varName);
        try
        {
            Environment.SetEnvironmentVariable(varName, target);
            Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, "%" + varName + "%");

            Assert.Equal(Path.GetFullPath(target), Path.GetFullPath(StorageRoot.Directory));
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, previousVar);
        }
    }

    [Fact]
    public void Override_Expands_A_Leading_Tilde()
    {
        var sub = "scriptdock-tilde-" + NanoId.New();
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, "~/" + sub);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.GetFullPath(Path.Combine(home, sub)), StorageRoot.Directory);
    }

    [Fact]
    public void Override_Expands_Dollar_Environment_References()
    {
        // See the note above: env var names stay within [A-Za-z0-9_].
        var varName = "SCRIPTDOCK_EXPAND_TEST_" + NanoId.New().Replace('-', '_');
        var target = Path.Combine(Path.GetTempPath(), "scriptdock-dollar-" + NanoId.New());
        var previousVar = Environment.GetEnvironmentVariable(varName);
        try
        {
            Environment.SetEnvironmentVariable(varName, target);

            Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, "$" + varName);
            Assert.Equal(Path.GetFullPath(target), Path.GetFullPath(StorageRoot.Directory));

            Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, "${" + varName + "}");
            Assert.Equal(Path.GetFullPath(target), Path.GetFullPath(StorageRoot.Directory));
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, previousVar);
        }
    }

    [Fact]
    public void Override_That_Expands_To_Empty_Is_Rejected()
    {
        // A reference to a variable that is definitely unset expands to empty; that is a
        // misconfiguration, reported rather than silently collapsing onto the home directory.
        // See the note above: env var names stay within [A-Za-z0-9_].
        var unsetVar = "SCRIPTDOCK_UNSET_PROBE_" + NanoId.New().Replace('-', '_');
        Environment.SetEnvironmentVariable(unsetVar, null);
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, "$" + unsetVar);

        Assert.Throws<InvalidOperationException>(() => _ = StorageRoot.Directory);
    }
}
