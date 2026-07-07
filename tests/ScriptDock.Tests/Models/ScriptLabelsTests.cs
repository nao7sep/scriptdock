using System.Collections.Generic;
using ScriptDock.Models;
using Xunit;

namespace ScriptDock.Tests.Models;

public sealed class ScriptLabelsTests
{
    [Fact]
    public void UniqueFileName_IsJustTheFileName()
    {
        var labels = ScriptLabels.Build(["/code/a/scripts/only.command"]);

        Assert.Equal("only.command", labels["/code/a/scripts/only.command"]);
    }

    [Fact]
    public void ConventionLayout_CompactsToAppSlashFile()
    {
        var a = "/code/bigmouth/scripts/run-dev.command";
        var b = "/code/daynote/scripts/run-dev.command";

        var labels = ScriptLabels.Build([a, b]);

        // "scripts" is a shared interior segment that does not disambiguate, so it is dropped.
        Assert.Equal("bigmouth/run-dev.command", labels[a]);
        Assert.Equal("daynote/run-dev.command", labels[b]);
    }

    [Fact]
    public void DangerousDrop_IsRejected_LabelsStayUniqueAndCorrect()
    {
        var a1 = "/x/a/scripts/run.command";
        var b1 = "/x/b/scripts/run.command";
        var a2 = "/x/a/run.command";

        var labels = ScriptLabels.Build([a1, b1, a2]);

        // Dropping "scripts" would make a/scripts/run and a/run collide, so it is kept.
        Assert.Equal("a/scripts/run.command", labels[a1]);
        Assert.Equal("b/scripts/run.command", labels[b1]);
        Assert.Equal("a/run.command", labels[a2]);
        Assert.Equal(3, new HashSet<string>(labels.Values).Count); // all unique
    }

    [Fact]
    public void DistinctParentDirs_DisambiguateWithoutAppName()
    {
        var s = "/x/app/scripts/run.command";
        var t = "/x/app/tools/run.command";

        var labels = ScriptLabels.Build([s, t]);

        Assert.Equal("scripts/run.command", labels[s]);
        Assert.Equal("tools/run.command", labels[t]);
    }

    [Fact]
    public void DeepCommonPrefix_DisambiguatesThenDropsSharedInterior()
    {
        var a = "/home/x/y/run.command";
        var b = "/home/z/y/run.command";

        var labels = ScriptLabels.Build([a, b]);

        // Suffixes deepen to the 'x' vs 'z' segment to disambiguate; the now-shared interior 'y' drops.
        Assert.Equal("x/run.command", labels[a]);
        Assert.Equal("z/run.command", labels[b]);
    }

    [Fact]
    public void RootLevelFiles_AreJustTheirFileNames()
    {
        var a = "/a.command";
        var b = "/b.command";

        var labels = ScriptLabels.Build([a, b]);

        Assert.Equal("a.command", labels[a]);
        Assert.Equal("b.command", labels[b]);
    }

    [Fact]
    public void MultipleSharedInteriorSegments_AreAllDropped()
    {
        var a = "/r/alpha/common/scripts/f.command";
        var b = "/r/beta/common/scripts/f.command";

        var labels = ScriptLabels.Build([a, b]);

        // Both 'common' and 'scripts' are shared interior segments that don't disambiguate, so both drop.
        Assert.Equal("alpha/f.command", labels[a]);
        Assert.Equal("beta/f.command", labels[b]);
    }

    [Fact]
    public void ResultIsIndependentOfInputOrder()
    {
        var p1 = "/r/alpha/common/f.command";
        var p2 = "/r/beta/common/f.command";
        var p3 = "/r/alpha/common/g.command";

        var forward = ScriptLabels.Build([p1, p2, p3]);
        var reversed = ScriptLabels.Build([p3, p2, p1]);

        foreach (var path in new[] { p1, p2, p3 })
            Assert.Equal(forward[path], reversed[path]);
    }

    [Fact]
    public void BackslashSeparators_AreNormalized()
    {
        var a = @"C:\code\alpha\run.command";
        var b = @"C:\code\beta\run.command";

        var labels = ScriptLabels.Build([a, b]);

        Assert.Equal("alpha/run.command", labels[a]);
        Assert.Equal("beta/run.command", labels[b]);
    }
}
