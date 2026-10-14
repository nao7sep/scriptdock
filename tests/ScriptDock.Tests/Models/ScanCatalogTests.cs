using System;
using System.IO;
using ScriptDock.Models;
using ScriptDock.Services;
using Xunit;

namespace ScriptDock.Tests.Models;

public sealed class ScanCatalogTests
{
    private static ScanReport Report(string unavailable, params string[] found) => new()
    {
        CompletedAt = DateTimeOffset.UtcNow, Roots = [unavailable], Found = found,
        Inaccessible = [unavailable], PrunedDirectories = [], SkippedFiles = [], InvalidPatterns = [],
    };

    [Fact]
    public void AnUnavailableFilesystemRootKeepsItsChildren()
    {
        var root = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!;
        var prior = Path.Combine(root, "script.command");
        Assert.Equal([prior], ScanCatalog.Paths(Report(root), [prior], path => path));
    }

    // Windows compares paths without case, so a differently cased spelling of an unavailable folder still keeps
    // its scripts, and one script found under two spellings is listed once; elsewhere case is significant.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PathCaseMattersOnlyWhereThePlatformComparesWithIt(bool ignoreCase)
    {
        var unavailable = Path.Combine(Path.GetTempPath(), "Vol", "Scripts");
        var report = Report(unavailable, Path.Combine(unavailable, "A.ps1"));
        var lower = Path.Combine(Path.GetTempPath(), "vol", "scripts");
        var previous = new[] { Path.Combine(lower, "a.ps1"), Path.Combine(lower, "c.ps1") };

        var kept = ScanCatalog.Paths(report, previous, path => path, ignoreCase);

        Assert.Equal(ignoreCase ? [report.Found[0], previous[1]] : [report.Found[0]], kept);
    }

    [MacOnlyFact]
    public void AnUnavailablePhysicalFolderKeepsPreviouslyDiscoveredAliasPaths()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var root = Path.Combine(Path.GetTempPath(), "scriptdock-scan-alias-" + Guid.NewGuid().ToString("N"));
        var physical = Directory.CreateDirectory(Path.Combine(root, "physical")).FullName;
        var alias = Path.Combine(root, "alias");
        Directory.CreateSymbolicLink(alias, physical);
        File.WriteAllText(Path.Combine(physical, "old.command"), "# fixture");
        var previous = new ScriptScanner().Scan([alias], [".command"], []).Found;
        var mode = File.GetUnixFileMode(physical);
        try
        {
            File.SetUnixFileMode(physical, UnixFileMode.None);
            var report = new ScriptScanner().Scan([physical], [".command"], []);
            Assert.Single(report.Inaccessible);
            Assert.Equal(previous, ScanCatalog.Paths(report, previous, PathIdentity.Key));
        }
        finally
        {
            File.SetUnixFileMode(physical, mode);
            Directory.Delete(root, true);
        }
    }

    [MacOnlyFact]
    public void AnUnavailableFolderKeepsTheVolumesEquivalentCaseSpelling()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var root = Path.Combine(Path.GetTempPath(), "scriptdock-scan-case-" + Guid.NewGuid().ToString("N"));
        var physical = Directory.CreateDirectory(Path.Combine(root, "physical")).FullName;
        File.WriteAllText(Path.Combine(physical, "old.command"), "# fixture");
        var alternate = Path.Combine(root, "PHYSICAL");
        var mode = File.GetUnixFileMode(physical);
        try
        {
            if (!Directory.Exists(alternate)) return; // A case-sensitive volume has distinct paths.
            var previous = new ScriptScanner().Scan([alternate], [".command"], []).Found;
            File.SetUnixFileMode(physical, UnixFileMode.None);
            var report = new ScriptScanner().Scan([physical], [".command"], []);
            Assert.Single(report.Inaccessible);
            Assert.Equal(previous, ScanCatalog.Paths(report, previous, PathIdentity.Key));
        }
        finally
        {
            File.SetUnixFileMode(physical, mode);
            Directory.Delete(root, true);
        }
    }

    [MacOnlyFact]
    public void DenialAboveAConfiguredRootKeepsEquivalentNestedSpellings()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var root = Path.Combine(Path.GetTempPath(), "scriptdock-scan-parent-" + Guid.NewGuid().ToString("N"));
        var physical = Directory.CreateDirectory(Path.Combine(root, "physical")).FullName;
        var alternate = Path.Combine(root, "PHYSICAL");
        File.WriteAllText(Path.Combine(physical, "old.command"), "# fixture");
        var mode = File.GetUnixFileMode(root);
        try
        {
            if (!Directory.Exists(alternate)) return;
            var previous = new ScriptScanner().Scan([physical], [".command"], []).Found;
            foreach (var denied in new[] { UnixFileMode.None, UnixFileMode.UserRead })
            {
                File.SetUnixFileMode(root, denied);
                var report = new ScriptScanner().Scan([alternate], [".command"], []);
                Assert.Equal(previous, ScanCatalog.Paths(report, previous, PathIdentity.Key));
                Assert.Contains(root, report.Inaccessible);
            }
        }
        finally
        {
            File.SetUnixFileMode(root, mode);
            Directory.Delete(root, true);
        }
    }

    [MacOnlyFact]
    public void AReadableNamedRootStillAppliesDeletionsWhenAnUnlistedSiblingIsMissing()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var root = Path.Combine(Path.GetTempPath(), "scriptdock-scan-search-" + Guid.NewGuid().ToString("N"));
        var observed = Directory.CreateDirectory(Path.Combine(root, "observed")).FullName;
        var missing = Path.Combine(root, "missing");
        var deleted = Path.Combine(observed, "deleted.command");
        var mode = File.GetUnixFileMode(root);
        try
        {
            File.SetUnixFileMode(root, UnixFileMode.UserExecute);
            var report = new ScriptScanner().Scan([observed, missing], [".command"], []);
            Assert.DoesNotContain(deleted, ScanCatalog.Paths(report, [deleted], PathIdentity.Key));
            Assert.Equal([missing], report.Inaccessible);
        }
        finally
        {
            File.SetUnixFileMode(root, mode);
            Directory.Delete(root, true);
        }
    }
}
