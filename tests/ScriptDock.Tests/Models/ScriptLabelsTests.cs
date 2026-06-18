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
}
