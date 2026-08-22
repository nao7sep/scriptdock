using System;
using System.IO;
using ScriptDock.Services;
using Xunit;

namespace ScriptDock.Tests.Services;

public sealed class PathIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scriptdock-path-id-" + Guid.NewGuid().ToString("N"));

    public PathIdentityTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [MacOnlyFact]
    public void Same_ResolvesAncestorSymlinkAliases()
    {
        var physical = Directory.CreateDirectory(Path.Combine(_root, "physical"));
        var script = Path.Combine(physical.FullName, "run.command");
        File.WriteAllText(script, "exit 0");
        var alias = Path.Combine(_root, "alias");
        Directory.CreateSymbolicLink(alias, physical.FullName);

        Assert.True(PathIdentity.Same(script, Path.Combine(alias, "run.command")));
    }

    [Fact]
    public void Same_UsesTheMountedFilesystemsActualCaseSemantics()
    {
        var lower = Path.Combine(_root, "case.command");
        var upper = Path.Combine(_root, "CASE.command");
        File.WriteAllText(lower, "lower");

        if (File.Exists(upper))
        {
            // This volume is case-insensitive: both spellings identify the existing file.
            Assert.True(PathIdentity.Same(lower, upper));
        }
        else
        {
            // This volume is case-sensitive: both files can exist and must remain distinct.
            File.WriteAllText(upper, "upper");
            Assert.False(PathIdentity.Same(lower, upper));
        }

        if (!OperatingSystem.IsWindows())
            Assert.False(PathIdentity.Comparer.Equals("case.command", "CASE.command"));
    }
}
