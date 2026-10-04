using System.Collections.Generic;
using System.IO;
using System.Linq;
using ScriptDock.Models;
using ScriptDock.Services;
using Xunit;

namespace ScriptDock.Tests.Models;

public sealed class ScanFlagsTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "scriptdock-flags-absent");

    private static string P(string name) => Path.Combine(Root, name);

    private static HashSet<string> Keys(params string[] names) =>
        new(names.Select(name => PathIdentity.Key(P(name))), PathIdentity.Comparer);

    [Fact]
    public void Full_FlagsExactlyItsOwnDiff()
    {
        var flags = ScanFlags.Full(new ScanDiff([P("b")], [P("x")]));

        Assert.True(flags.NewKeys.SetEquals(Keys("b")));
        Assert.Equal([P("x")], flags.Removed);
    }

    [Fact]
    public void Background_KeepsEarlierFlags_AndAddsItsOwn()
    {
        var flags = ScanFlags.Background(
            new ScanDiff([P("c")], [P("y")]),
            found: [P("a"), P("b"), P("c")],
            newKeys: Keys("a"),
            removed: [P("x")]);

        Assert.True(flags.NewKeys.SetEquals(Keys("a", "c")));
        Assert.Equal([P("x"), P("y")], flags.Removed);
    }

    [Fact]
    public void Background_DropsANewFlagForAScriptNowGone_AndARemovedFlagForOneFoundAgain()
    {
        var flags = ScanFlags.Background(
            new ScanDiff([P("x")], [P("a")]),
            found: [P("x")],
            newKeys: Keys("a"),
            removed: [P("x")]);

        // a was new and is gone: it is removed now, not new. x was removed and is back: it is new again.
        Assert.True(flags.NewKeys.SetEquals(Keys("x")));
        Assert.Equal([P("a")], flags.Removed);
    }
}
