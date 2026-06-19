using System;
using System.Collections.Generic;
using ScriptDock.Models;
using ScriptDock.Services;
using Xunit;

namespace ScriptDock.Tests.Services;

public sealed class ScriptListBuilderTests
{
    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, string> Labels(params (string Path, string Label)[] entries)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, label) in entries)
            map[path] = label;
        return map;
    }

    [Fact]
    public void BuildScripts_FlagsNew_MarksRunning_SortsByPath()
    {
        var bigmouth = "/code/bigmouth/scripts/run-dev.command";
        var daynote = "/code/daynote/scripts/run-dev.command";

        var scripts = ScriptListBuilder.BuildScripts(
            found: [daynote, bigmouth],
            removed: [],
            hidden: Set(),
            newPaths: Set(daynote),
            runningPaths: Set(bigmouth),
            labels: Labels((bigmouth, "bigmouth/run-dev.command"), (daynote, "daynote/run-dev.command")),
            showHidden: false);

        Assert.Equal(2, scripts.Count);
        Assert.Equal("bigmouth/run-dev.command", scripts[0].DisplayName); // /code/bigmouth/… sorts before /code/daynote/… by path
        Assert.True(scripts[0].IsRunning);
        Assert.Equal(ScriptFlag.None, scripts[0].Flag);
        Assert.Equal("daynote/run-dev.command", scripts[1].DisplayName);
        Assert.Equal(ScriptFlag.New, scripts[1].Flag);
        Assert.False(scripts[1].IsRunning);
    }

    [Fact]
    public void BuildScripts_HidesHidden_UnlessShowHidden()
    {
        var hiddenPath = "/code/x/scripts/run-dev.command";
        var labels = Labels((hiddenPath, "x/run-dev.command"));

        var withoutHidden = ScriptListBuilder.BuildScripts([hiddenPath], [], Set(hiddenPath), Set(), Set(), labels, showHidden: false);
        Assert.Empty(withoutHidden);

        var withHidden = ScriptListBuilder.BuildScripts([hiddenPath], [], Set(hiddenPath), Set(), Set(), labels, showHidden: true);
        Assert.Single(withHidden);
        Assert.True(withHidden[0].IsHidden);
    }

    [Fact]
    public void BuildScripts_SurfacesRemoved_EvenWhenHiddenFilterOn()
    {
        var removed = "/code/x/scripts/gone.command";

        var scripts = ScriptListBuilder.BuildScripts([], [removed], Set(), Set(), Set(), Labels((removed, "x/gone.command")), showHidden: false);

        Assert.Single(scripts);
        Assert.Equal(ScriptFlag.Removed, scripts[0].Flag);
    }
}
