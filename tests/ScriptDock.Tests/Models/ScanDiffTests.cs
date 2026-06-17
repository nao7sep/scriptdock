using ScriptDock.Models;
using Xunit;

namespace ScriptDock.Tests.Models;

public sealed class ScanDiffTests
{
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
