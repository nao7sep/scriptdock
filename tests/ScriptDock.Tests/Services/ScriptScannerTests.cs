using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using ScriptDock;
using ScriptDock.Services;
using ScriptDock.Tests.Fakes;
using Xunit;

namespace ScriptDock.Tests.Services;

/// <summary>
/// Exercises the scanner against a real temp directory tree (no global state, so these run
/// in parallel safely). Covers the three classifications — found, pruned directory, skipped
/// file — plus extension filtering, invalid-pattern collection, and missing roots.
/// </summary>
public sealed class ScriptScannerTests : IDisposable
{
    private readonly string _root;

    public ScriptScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "scriptdock-scan-tests", NanoId.New());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private void Touch(string relativePath)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "#");
    }

    [Fact]
    public void Scan_FindsCandidates_PrunesDirs_SkipsFiles_FiltersByExtension()
    {
        Touch("a.command");
        Touch("keep/b.command");
        Touch("node_modules/c.command");
        Touch("ignore-me.command");
        Touch("notes.txt");

        var report = new ScriptScanner().Scan(
            rootDirs: [_root],
            extensions: [".command"],
            ignorePatterns: ["/node_modules/", "ignore-me"],
            cancellationToken: TestContext.Current.CancellationToken);

        var foundNames = report.Found.Select(Path.GetFileName).ToList();
        Assert.Contains("a.command", foundNames);
        Assert.Contains("b.command", foundNames);
        Assert.DoesNotContain("c.command", foundNames);         // inside a pruned directory
        Assert.DoesNotContain("ignore-me.command", foundNames); // skipped by pattern
        Assert.DoesNotContain("notes.txt", foundNames);         // wrong extension

        Assert.Contains(report.PrunedDirectories, e => Path.GetFileName(e.Path) == "node_modules" && e.Pattern == "/node_modules/");
        Assert.Contains(report.SkippedFiles, e => Path.GetFileName(e.Path) == "ignore-me.command" && e.Pattern == "ignore-me");
        Assert.Empty(report.InvalidPatterns);
    }

    [Fact]
    public void Scan_CollectsInvalidPatterns_AndStillScans()
    {
        Touch("a.command");

        var report = new ScriptScanner().Scan([_root], [".command"], ["["], TestContext.Current.CancellationToken);

        Assert.Contains("[", report.InvalidPatterns);
        Assert.Contains(report.Found, p => Path.GetFileName(p) == "a.command");
    }

    [Fact]
    public void Scan_MissingRoot_IsRecordedInaccessible()
    {
        var missing = Path.Combine(_root, "does-not-exist");

        var report = new ScriptScanner().Scan([missing], [".command"], [], TestContext.Current.CancellationToken);

        Assert.Contains(Path.GetFullPath(missing), report.Inaccessible);
        Assert.Empty(report.Found);
    }

    [Fact]
    public void Scan_NormalisesDotlessExtension_FromHandEditedConfig()
    {
        Touch("a.command");

        // A config.json hand-edited to "command" (no leading dot) must still match — Path.GetExtension
        // always yields a dotted form, so the scanner normalizes the configured extension.
        var report = new ScriptScanner().Scan([_root], ["command"], [], TestContext.Current.CancellationToken);

        Assert.Contains(report.Found, p => Path.GetFileName(p) == "a.command");
    }

    [Fact]
    public void Scan_OverlappingRoots_ListEachScriptOnce()
    {
        Touch("sub/x.command");
        var sub = Path.Combine(_root, "sub");

        // The inner script is reachable from both roots; it must be found exactly once, not duplicated.
        var report = new ScriptScanner().Scan([_root, sub], [".command"], [], TestContext.Current.CancellationToken);

        Assert.Single(report.Found, p => Path.GetFileName(p) == "x.command");
    }

    [MacOnlyFact]
    public void Scan_DoesNotDescendSymbolicLinks()
    {
        Touch("real/x.command");
        Directory.CreateSymbolicLink(Path.Combine(_root, "link"), Path.Combine(_root, "real"));

        var report = new ScriptScanner().Scan([_root], [".command"], [], TestContext.Current.CancellationToken);

        // Found once, via the real directory only — the symlink is not followed (no loop, no dup).
        Assert.Single(report.Found, p => Path.GetFileName(p) == "x.command");
        Assert.DoesNotContain(report.Found, p => p.Contains($"{Path.DirectorySeparatorChar}link{Path.DirectorySeparatorChar}"));
    }

    [WindowsOnlyFact]
    public void Scan_DoesNotDescendJunctions()
    {
        Touch("real/x.ps1");
        var alias = Path.Combine(_root, "link");
        WindowsJunction.Create(alias, Path.Combine(_root, "real"));
        try
        {
            var report = new ScriptScanner().Scan([_root], [".ps1"], [], TestContext.Current.CancellationToken);
            Assert.Equal(Path.Combine(_root, "real", "x.ps1"), Assert.Single(report.Found));
        }
        finally { Directory.Delete(alias); }
    }

    [WindowsOnlyFact]
    public void Scan_RecordsAnAclDeniedFolderAndKeepsGoing()
    {
        if (!OperatingSystem.IsWindows()) return; // The attribute and analyzer recognize different guards.
        Touch("ok/x.ps1");
        Touch("locked/y.ps1");
        var locked = new DirectoryInfo(Path.Combine(_root, "locked"));
        var original = locked.GetAccessControl();
        var denied = locked.GetAccessControl();
        using var identity = WindowsIdentity.GetCurrent();
        denied.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
        try
        {
            locked.SetAccessControl(denied);
            // Prove the fixture denied enumeration, rather than passing without testing that boundary.
            Assert.Throws<UnauthorizedAccessException>(() => Directory.GetFileSystemEntries(locked.FullName));
            var report = new ScriptScanner().Scan([_root], [".ps1"], [], TestContext.Current.CancellationToken);
            Assert.Contains(Path.Combine(_root, "ok", "x.ps1"), report.Found);
            Assert.Contains(locked.FullName, report.Inaccessible);
            Assert.DoesNotContain(Path.Combine(locked.FullName, "y.ps1"), report.Found);
        }
        finally { locked.SetAccessControl(original); }
    }

    [MacOnlyFact]
    public void Scan_RecordsMidWalkInaccessibleDirectory_AndKeepsGoing()
    {
        Touch("ok/x.command");
        Touch("locked/y.command");
        var locked = Path.Combine(_root, "locked");
        if (OperatingSystem.IsWindows())
            return; // MacOnlyFact already skips Windows; this guard is what the CA1416 analyzer recognizes
        File.SetUnixFileMode(locked, UnixFileMode.None); // 000: enumerating it throws, must not abort the scan
        try
        {
            var report = new ScriptScanner().Scan([_root], [".command"], [], TestContext.Current.CancellationToken);

            Assert.Contains(report.Found, p => Path.GetFileName(p) == "x.command");        // sibling still scanned
            Assert.Contains(report.Inaccessible, p => Path.GetFileName(p) == "locked");    // recorded, not fatal
            Assert.DoesNotContain(report.Found, p => Path.GetFileName(p) == "y.command");
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
