using System;
using System.IO;
using System.Threading;
using ScriptDock;
using ScriptDock.Services;
using Xunit;

namespace ScriptDock.Tests.Services;

public sealed class LogRevealTests
{
    [Fact]
    public void SelectTarget_prefers_the_session_log_over_a_newer_scan_report()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Path);
        var sessionLog = Path.Combine(temp.Path, "20260610-093015-utc.log");
        File.WriteAllText(sessionLog, "{}\n");
        // A scan report written later is the newest *.log, so the newest-file heuristic would pick it.
        Thread.Sleep(10);
        File.WriteAllText(Path.Combine(temp.Path, "scan-20260610-093020-utc.log"), "report\n");

        var target = LogReveal.SelectTarget(temp.Path, sessionLog, () => { });

        Assert.Equal(LogRevealTargetKind.File, target.Kind);
        Assert.Equal(sessionLog, target.Path); // the session log, not the newer scan report
    }

    [Fact]
    public void SelectTarget_flushes_before_looking_for_the_current_log()
    {
        using var temp = new TempDirectory();
        var logPath = Path.Combine(temp.Path, "20260610-093015-utc.log");

        // No session-log path supplied (logging fell back to console), so it finds the newest *.log,
        // which the flush callback writes — proving the flush runs before the lookup.
        var target = LogReveal.SelectTarget(temp.Path, sessionLogPath: null, () =>
        {
            Directory.CreateDirectory(temp.Path);
            File.WriteAllText(logPath, "{}\n");
        });

        Assert.Equal(LogRevealTargetKind.File, target.Kind);
        Assert.Equal(logPath, target.Path);
    }

    [Fact]
    public void SelectTarget_falls_back_to_newest_when_session_log_path_is_missing()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Path);
        var present = Path.Combine(temp.Path, "20260610-093015-utc.log");
        File.WriteAllText(present, "{}\n");
        var missing = Path.Combine(temp.Path, "does-not-exist.log");

        var target = LogReveal.SelectTarget(temp.Path, missing, () => { });

        Assert.Equal(LogRevealTargetKind.File, target.Kind);
        Assert.Equal(present, target.Path);
    }

    [Fact]
    public void SelectTarget_uses_the_logs_directory_when_no_log_exists()
    {
        using var temp = new TempDirectory();

        var target = LogReveal.SelectTarget(temp.Path, sessionLogPath: null, () => { });

        Assert.Equal(LogRevealTargetKind.Directory, target.Kind);
        Assert.Equal(temp.Path, target.Path);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "scriptdock-logreveal-tests",
                NanoId.New());
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }
}
