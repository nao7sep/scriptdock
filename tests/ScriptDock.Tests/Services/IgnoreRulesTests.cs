using ScriptDock.Services;
using Xunit;
using System;
using System.Diagnostics;
using System.Threading;

namespace ScriptDock.Tests.Services;

public sealed class IgnoreRulesTests
{
    [Fact]
    public void TimedOutRule_IsReportedAndDisabledForTheRestOfTheScan()
    {
        var rules = IgnoreRules.Compile(["(a+)+$", "safe$"], TimeSpan.FromMilliseconds(10));
        var hostile = "/" + new string('a', 20_000) + "!";

        Assert.Null(rules.FirstMatch(hostile));
        Assert.Contains("(a+)+$", rules.InvalidPatterns);

        var stopwatch = Stopwatch.StartNew();
        Assert.Equal("safe$", rules.FirstMatch("/safe"));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public void CancellableMatch_ObservesCancellationBetweenRules()
    {
        var rules = IgnoreRules.Compile(["one", "two"]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => rules.FirstMatchCancellable("/x", cts.Token));
    }

    [Fact]
    public void SlashWrappedPattern_MatchesDirectoryPathWithTrailingSlash()
    {
        var rules = IgnoreRules.Compile(["/node_modules/"]);

        Assert.Equal("/node_modules/", rules.FirstMatch("/Users/x/proj/node_modules/"));
        Assert.Null(rules.FirstMatch("/Users/x/proj/src/"));
    }

    [Fact]
    public void Pattern_MatchesFilePath()
    {
        var rules = IgnoreRules.Compile(["/bin/"]);

        Assert.Equal("/bin/", rules.FirstMatch("/Users/x/proj/bin/tool.command"));
    }

    [Fact]
    public void Matching_NormalisesBackslashesAndIsCaseInsensitive()
    {
        var rules = IgnoreRules.Compile(["/node_modules/"]);

        Assert.Equal("/node_modules/", rules.FirstMatch(@"C:\x\Node_Modules\pkg\"));
    }

    [Fact]
    public void InvalidPattern_IsCollected_NotThrown()
    {
        var rules = IgnoreRules.Compile(["[", "/obj/"]);

        Assert.Contains("[", rules.InvalidPatterns);
        Assert.Equal("/obj/", rules.FirstMatch("/x/obj/"));
    }

    [Fact]
    public void NoMatch_ReturnsNull()
    {
        var rules = IgnoreRules.Compile(["/obj/"]);

        Assert.Null(rules.FirstMatch("/x/src/main.command"));
    }
}
