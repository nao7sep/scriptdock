using System;
using System.Collections.Generic;
using ScriptDock.Models;
using ScriptDock.Services;
using Xunit;

namespace ScriptDock.Tests.Services;

public sealed class ScriptListBuilderTests
{
    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);

    [Fact]
    public void BuildScripts_FlagsNew_MarksRunning_SortsByDisplayName()
    {
        var bigmouth = "/code/bigmouth/scripts/run-dev.command";
        var aholist = "/code/aholist/scripts/run-dev.command";

        var scripts = ScriptListBuilder.BuildScripts(
            found: [bigmouth, aholist],
            removed: [],
            hidden: Set(),
            newPaths: Set(bigmouth),
            runningPaths: Set(aholist),
            showHidden: false);

        Assert.Equal(2, scripts.Count);
        Assert.Equal("aholist/run-dev.command", scripts[0].DisplayName); // sorts first
        Assert.True(scripts[0].IsRunning);
        Assert.Equal(ScriptFlag.None, scripts[0].Flag);
        Assert.Equal("bigmouth/run-dev.command", scripts[1].DisplayName);
        Assert.Equal(ScriptFlag.New, scripts[1].Flag);
        Assert.False(scripts[1].IsRunning);
    }

    [Fact]
    public void BuildScripts_HidesHidden_UnlessShowHidden()
    {
        var hiddenPath = "/code/x/scripts/run-dev.command";

        var withoutHidden = ScriptListBuilder.BuildScripts([hiddenPath], [], Set(hiddenPath), Set(), Set(), showHidden: false);
        Assert.Empty(withoutHidden);

        var withHidden = ScriptListBuilder.BuildScripts([hiddenPath], [], Set(hiddenPath), Set(), Set(), showHidden: true);
        Assert.Single(withHidden);
        Assert.True(withHidden[0].IsHidden);
    }

    [Fact]
    public void BuildScripts_SurfacesRemoved_EvenWhenHiddenFilterOn()
    {
        var removed = "/code/x/scripts/gone.command";

        var scripts = ScriptListBuilder.BuildScripts([], [removed], Set(), Set(), Set(), showHidden: false);

        Assert.Single(scripts);
        Assert.Equal(ScriptFlag.Removed, scripts[0].Flag);
    }
}
