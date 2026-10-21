using ScriptDock.Models;
using ScriptDock.Services;
using Xunit;
using System;
using System.Diagnostics;
using System.Threading;

namespace ScriptDock.Tests.Services;

/// <summary>
/// Ignore patterns as a scan checks them: a wildcard reads names, or a path from the root directory when it
/// holds a slash; a regular expression reads the full path; a trailing slash means folders only, and case
/// never matters.
/// </summary>
public sealed class IgnoreRulesTests
{
    private const string Root = "/Users/x/code";

    private static IgnorePattern Wildcard(string pattern) => new(pattern);
    private static IgnorePattern Regex(string pattern) => new(pattern, IgnorePatternKind.Regex);

    private static string? Folder(IgnoreRules rules, string path) => rules.FirstMatch(Root, Root + path, isDirectory: true)?.Pattern;
    private static string? File(IgnoreRules rules, string path) => rules.FirstMatch(Root, Root + path, isDirectory: false)?.Pattern;

    [Fact]
    public void A_wildcard_name_matches_at_any_depth_and_ignores_case()
    {
        var rules = IgnoreRules.Compile([Wildcard("node_modules/")]);

        Assert.Equal("node_modules/", Folder(rules, "/node_modules"));
        Assert.Equal("node_modules/", Folder(rules, "/web/app/Node_Modules"));
        Assert.Null(Folder(rules, "/web/node_modules_old"));
        Assert.Null(Folder(rules, "/web/my_node_modules"));
    }

    [Fact]
    public void A_trailing_slash_matches_folders_only()
    {
        var folders = IgnoreRules.Compile([Wildcard("cache/"), Regex("/cache/")]);
        var both = IgnoreRules.Compile([Wildcard("cache")]);

        Assert.Null(File(folders, "/cache"));
        Assert.Equal("cache/", Folder(folders, "/cache"));
        Assert.Equal("cache", File(both, "/cache"));
        Assert.Equal("cache", Folder(both, "/cache"));
    }

    [Theory]
    [InlineData("*.bak.command", "/a/b/old.bak.command", true)]
    [InlineData("*.bak.command", "/a/b/old.command", false)]
    [InlineData("run-?.command", "/run-1.command", true)]
    [InlineData("run-?.command", "/run-10.command", false)]
    [InlineData("$RECYCLE.BIN", "/$recycle.bin", true)]
    [InlineData("$RECYCLE.BIN", "/$RECYCLExBIN", false)]
    [InlineData("System Volume Information", "/System Volume Information", true)]
    public void Star_and_question_mark_stand_within_a_name_and_every_other_character_for_itself(string pattern, string path, bool matches) =>
        Assert.Equal(matches, File(IgnoreRules.Compile([Wildcard(pattern)]), path) is not null);

    [Theory]
    [InlineData("tools/old/", "/tools/old", true)]
    [InlineData("tools/old/", "/proj/tools/old", false)]
    [InlineData("/tools/", "/tools", true)]
    [InlineData("/tools/", "/proj/tools", false)]
    [InlineData("**/tools/old/", "/proj/deep/tools/old", true)]
    [InlineData("**/tools/old/", "/tools/old", true)]
    [InlineData("tools/*/", "/tools/one", true)]
    [InlineData("tools/*/", "/tools/one/two", false)]
    [InlineData("tools/**/", "/tools/one/two", true)]
    public void A_wildcard_with_a_slash_in_it_matches_from_the_root_directory(string pattern, string path, bool matches) =>
        Assert.Equal(matches, Folder(IgnoreRules.Compile([Wildcard(pattern)]), path) is not null);

    [Fact]
    public void A_regular_expression_reads_the_full_path_with_forward_slashes()
    {
        var rules = IgnoreRules.Compile([Regex("/bin/(Debug|Release)/")]);

        Assert.Equal("/bin/(Debug|Release)/", rules.FirstMatch(@"C:\u\app", @"C:\u\app\bin\release", isDirectory: true)?.Pattern);
        Assert.Equal("/bin/(Debug|Release)/", Folder(rules, "/app/bin/Debug"));
        Assert.Null(Folder(rules, "/app/bin"));
        Assert.Null(Folder(rules, "/app/mybin/Debug2"));
    }

    [Fact]
    public void The_first_matching_pattern_names_why()
    {
        var rules = IgnoreRules.Compile([Wildcard("*.command"), Wildcard("old/")]);

        Assert.Equal("*.command", File(rules, "/old.command"));
        Assert.Equal("old/", Folder(rules, "/old"));
        Assert.Null(File(rules, "/old.sh"));
    }

    [Theory]
    [InlineData("[", IgnorePatternKind.Regex, false)]
    [InlineData("(a", IgnorePatternKind.Regex, false)]
    [InlineData("/", IgnorePatternKind.Wildcard, false)]
    [InlineData("//", IgnorePatternKind.Wildcard, false)]
    [InlineData("[", IgnorePatternKind.Wildcard, true)]
    [InlineData("(a", IgnorePatternKind.Wildcard, true)]
    [InlineData("/obj/", IgnorePatternKind.Regex, true)]
    public void Only_a_pattern_that_compiles_is_valid(string pattern, IgnorePatternKind kind, bool valid) =>
        Assert.Equal(valid, IgnoreRules.Compiles(new IgnorePattern(pattern, kind)));

    [Fact]
    public void An_invalid_pattern_is_collected_not_thrown()
    {
        var rules = IgnoreRules.Compile([Regex("["), Wildcard("obj/")]);

        Assert.Contains("[", rules.InvalidPatterns);
        Assert.Equal("obj/", Folder(rules, "/x/obj"));
    }

    [Fact]
    public void A_timed_out_rule_is_reported_and_disabled_for_the_rest_of_the_scan()
    {
        var rules = IgnoreRules.Compile([Regex("(a+)+$"), Regex("safe$")], TimeSpan.FromMilliseconds(10));
        var hostile = "/" + new string('a', 20_000) + "!";

        Assert.Null(rules.FirstMatch("/", hostile, isDirectory: false));
        Assert.Contains("(a+)+$", rules.InvalidPatterns);

        var stopwatch = Stopwatch.StartNew();
        Assert.Equal("safe$", rules.FirstMatch("/", "/safe", isDirectory: false)?.Pattern);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public void A_cancellable_match_observes_cancellation_between_rules()
    {
        var rules = IgnoreRules.Compile([Wildcard("one"), Wildcard("two")]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => rules.FirstMatchCancellable("/", "/x", isDirectory: false, cts.Token));
    }
}
