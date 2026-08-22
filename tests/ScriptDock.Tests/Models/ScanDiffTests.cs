using ScriptDock.Models;
using Xunit;
using System;
using System.IO;

namespace ScriptDock.Tests.Models;

public sealed class ScanDiffTests
{
    [MacOnlyFact]
    public void Compute_DoesNotReportPhysicalAliasAsAddedAndRemoved()
    {
        var root = Path.Combine(Path.GetTempPath(), "scriptdock-diff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var physical = Directory.CreateDirectory(Path.Combine(root, "physical"));
            var script = Path.Combine(physical.FullName, "run.command");
            File.WriteAllText(script, "exit 0");
            var alias = Path.Combine(root, "alias");
            Directory.CreateSymbolicLink(alias, physical.FullName);

            var diff = ScanDiff.Compute([script], [Path.Combine(alias, "run.command")]);

            Assert.Empty(diff.Added);
            Assert.Empty(diff.Removed);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Compute_ReportsAddedAndRemoved()
    {
        var diff = ScanDiff.Compute(found: ["/a", "/b", "/c"], known: ["/b", "/c", "/d"]);

        Assert.Equal(["/a"], diff.Added);
        Assert.Equal(["/d"], diff.Removed);
    }

    [Fact]
    public void Compute_EmptyKnown_AllAreAdded()
    {
        var diff = ScanDiff.Compute(["/a", "/b"], []);

        Assert.Equal(["/a", "/b"], diff.Added);
        Assert.Empty(diff.Removed);
    }

    [Fact]
    public void Compute_Identical_NoChange()
    {
        var diff = ScanDiff.Compute(["/a", "/b"], ["/b", "/a"]);

        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
    }
}
