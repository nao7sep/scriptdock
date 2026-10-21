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

    private static IgnorePattern? Match(string path, bool isDirectory)
    {
        var root = path.StartsWith("C:", StringComparison.Ordinal) ? @"C:\u" : "/u";
        return BuiltIns.FirstMatch(root, path.TrimEnd('/', '\\'), isDirectory);
    }

    // Each built-in skips a downloaded or generated tree, or a system folder, on either platform's separators.
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
    [InlineData("/u/.Trash/")]
    [InlineData("/u/Volumes/Drive/.Trashes/")]
    [InlineData("/u/Downloads/archive/__MACOSX/")]
    [InlineData("/u/Volumes/Drive/.Spotlight-V100/")]
    [InlineData("/u/Volumes/Drive/.fseventsd/")]
    [InlineData("/u/Volumes/Drive/.TemporaryItems/")]
    [InlineData("/u/Volumes/Drive/.DocumentRevisions-V100/")]
    [InlineData("/u/.MobileBackups/")]
    [InlineData("/u/share/.AppleDouble/")]
    [InlineData(@"C:\u\$Recycle.Bin\")]
    [InlineData(@"C:\u\RECYCLER\")]
    [InlineData(@"C:\u\RECYCLED\")]
    [InlineData(@"C:\u\System Volume Information\")]
    public void The_built_ins_skip_generated_downloaded_and_system_folders(string folder) =>
        Assert.NotNull(Match(folder, isDirectory: true));

    // Folders whose names a project may use for its own scripts stay scanned, and so does a script named
    // like a skipped folder: every built-in matches folders only.
    [Theory]
    [InlineData("/u/proj/bin/", true)]
    [InlineData("/u/proj/bin/deploy.command", false)]
    [InlineData("/u/proj/build/", true)]
    [InlineData("/u/proj/dist/", true)]
    [InlineData("/u/proj/out/", true)]
    [InlineData("/u/proj/scripts/", true)]
    [InlineData("/u/proj/tools/run.command", false)]
    [InlineData("/u/proj/vendor", false)]
    [InlineData("/u/proj/Trash/", true)]
    public void The_built_ins_leave_folders_that_can_hold_real_scripts(string path, bool isDirectory) =>
        Assert.Null(Match(path, isDirectory));

    [Fact]
    public void Every_built_in_is_a_valid_folder_pattern_listed_once_with_a_note()
    {
        Assert.Empty(BuiltIns.InvalidPatterns);
        Assert.All(ConfigDefaults.BuiltInIgnorePatterns, pattern =>
        {
            Assert.True(pattern.FoldersOnly, pattern.Pattern);
            Assert.True(ConfigDefaults.IgnoreNoteKeys.ContainsKey(pattern.Id!), pattern.Pattern);
        });
        Assert.Equal(ConfigDefaults.BuiltInIgnorePatterns.Count,
            ConfigDefaults.BuiltInIgnorePatterns.Select(ConfigSets.PatternKey).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(ConfigDefaults.BuiltInIgnorePatterns.Count,
            ConfigDefaults.BuiltInIgnorePatterns.Select(pattern => pattern.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(ConfigDefaults.IgnoreNoteKeys.Keys.Order(StringComparer.Ordinal),
            ConfigDefaults.BuiltInIgnorePatterns.Select(pattern => pattern.Id!).Order(StringComparer.Ordinal));
    }

    // The order the list reads in: development folders, then macOS's, then Windows's; only the .NET build
    // output is a regular expression.
    [Fact]
    public void The_built_ins_read_in_their_order()
    {
        Assert.Equal(
            [".git/", "node_modules/", ".venv/", "venv/", "__pycache__/", "/bin/(Debug|Release)/", "obj/", ".gradle/", "target/",
             "vendor/", ".build/", "Pods/", ".Trash/", ".Trashes/", "__MACOSX/", ".Spotlight-V100/", ".fseventsd/",
             ".TemporaryItems/", ".DocumentRevisions-V100/", ".MobileBackups/", ".AppleDouble/", "$RECYCLE.BIN/",
             "RECYCLER/", "RECYCLED/", "System Volume Information/"],
            ConfigDefaults.BuiltInIgnorePatterns.Select(pattern => pattern.Pattern));
        Assert.Equal(["dotnet.bin"],
            ConfigDefaults.BuiltInIgnorePatterns.Where(pattern => pattern.Kind == IgnorePatternKind.Regex).Select(pattern => pattern.Id));
    }
}
