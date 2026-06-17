using System;
using System.Collections.Generic;
using System.Linq;
using ScriptDock.Models;
using ScriptDock.Services;
using Xunit;

namespace ScriptDock.Tests.Services;

public sealed class ScriptListBuilderTests
{
    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);

    [Fact]
    public void BuildScripts_FlagsNewAndFavorite_SortsByDisplayName()
    {
        var scripts = ScriptListBuilder.BuildScripts(
            found: ["/code/bigmouth/scripts/run-dev.command", "/code/aholist/scripts/run-dev.command"],
            removed: [],
            favorites: Set("/code/aholist/scripts/run-dev.command"),
            hidden: Set(),
            newPaths: Set("/code/bigmouth/scripts/run-dev.command"),
            showHidden: false);

        Assert.Equal(2, scripts.Count);
        Assert.Equal("aholist/run-dev.command", scripts[0].DisplayName); // sorts first
        Assert.True(scripts[0].IsFavorite);
        Assert.Equal(ScriptFlag.None, scripts[0].Flag);
        Assert.Equal("bigmouth/run-dev.command", scripts[1].DisplayName);
        Assert.Equal(ScriptFlag.New, scripts[1].Flag);
    }

    [Fact]
    public void BuildScripts_HidesHidden_UnlessShowHidden()
    {
        var hiddenPath = "/code/x/scripts/run-dev.command";

        var withoutHidden = ScriptListBuilder.BuildScripts(
            [hiddenPath], [], Set(), Set(hiddenPath), Set(), showHidden: false);
        Assert.Empty(withoutHidden);

        var withHidden = ScriptListBuilder.BuildScripts(
            [hiddenPath], [], Set(), Set(hiddenPath), Set(), showHidden: true);
        Assert.Single(withHidden);
        Assert.True(withHidden[0].IsHidden);
    }

    [Fact]
    public void BuildScripts_SurfacesRemoved_EvenWhenHiddenFilterOn()
    {
        var removed = "/code/x/scripts/gone.command";

        var scripts = ScriptListBuilder.BuildScripts(
            found: [], removed: [removed], favorites: Set(), hidden: Set(), newPaths: Set(), showHidden: false);

        Assert.Single(scripts);
        Assert.Equal(ScriptFlag.Removed, scripts[0].Flag);
    }

    [Fact]
    public void BuildFavorites_FlagsMissingAsRemoved()
    {
        var present = "/code/a/scripts/run-dev.command";
        var gone = "/code/b/scripts/run-dev.command";

        var favorites = ScriptListBuilder.BuildFavorites([present, gone], Set(present));

        Assert.Equal(2, favorites.Count);
        Assert.Equal(ScriptFlag.None, favorites.Single(f => f.Path == present).Flag);
        Assert.Equal(ScriptFlag.Removed, favorites.Single(f => f.Path == gone).Flag);
    }
}
