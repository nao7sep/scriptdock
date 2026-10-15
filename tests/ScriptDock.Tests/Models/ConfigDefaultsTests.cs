using System;
using System.Linq;
using ScriptDock.Models;
using ScriptDock.Services;
using Xunit;

namespace ScriptDock.Tests.Models;

public sealed class ConfigDefaultsTests
{
    [Fact]
    public void DefaultExtension_MatchesPlatform()
    {
        var expected = OperatingSystem.IsWindows() ? ".ps1" : ".command";

        Assert.Equal(expected, ConfigDefaults.DefaultExtension);
    }

    private static readonly IgnoreRules BuiltIns = IgnoreRules.Compile(ConfigDefaults.BuiltInIgnorePatterns);

    // Each built-in skips a downloaded or generated tree, on either platform's separators.
    [Theory]
    [InlineData("/u/proj/.git/")]
    [InlineData("/u/web/node_modules/")]
    [InlineData("/u/py/.venv/")]
    [InlineData("/u/py/venv/")]
    [InlineData("/u/py/pkg/__pycache__/")]
    [InlineData("/u/app/bin/Debug/")]
    [InlineData(@"C:\u\app\bin\Release\")]
    [InlineData("/u/app/obj/")]
    [InlineData("/u/android/.gradle/")]
    [InlineData("/u/crate/target/")]
    [InlineData("/u/site/vendor/")]
    [InlineData("/u/pkg/.build/")]
    [InlineData("/u/ios/Pods/")]
    public void The_built_ins_skip_generated_and_downloaded_trees(string folder) =>
        Assert.NotNull(BuiltIns.FirstMatch(folder));

    // Folders whose names a project may use for its own scripts stay scanned.
    [Theory]
    [InlineData("/u/proj/bin/")]
    [InlineData("/u/proj/bin/deploy.command")]
    [InlineData("/u/proj/build/")]
    [InlineData("/u/proj/dist/")]
    [InlineData("/u/proj/out/")]
    [InlineData("/u/proj/scripts/")]
    [InlineData("/u/proj/tools/run.command")]
    public void The_built_ins_leave_folders_that_can_hold_real_scripts(string path) =>
        Assert.Null(BuiltIns.FirstMatch(path));

    [Fact]
    public void Every_built_in_is_a_valid_pattern_listed_once()
    {
        Assert.Empty(BuiltIns.InvalidPatterns);
        Assert.Equal(ConfigDefaults.BuiltInIgnorePatterns.Count,
            ConfigDefaults.BuiltInIgnorePatterns.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
