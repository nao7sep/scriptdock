using System;
using System.IO;
using System.Linq;
using System.Threading;
using ScriptDock.Models;
using ScriptDock.Services;
using Xunit;

namespace ScriptDock.Tests.Services;

/// <summary>
/// Integration tests for the runner: they launch real processes through the login shell with
/// output redirected to a per-run file, so they run on macOS only. They cover the
/// launch + file-capture path, a clean exit, and process-tree termination of a long-running
/// script. The runs directory is injected so these never touch the real <c>~/.scriptdock</c>.
/// </summary>
public sealed class ProcessRunnerTests : IDisposable
{
    private readonly string _dir;
    private readonly string _runsDir;

    public ProcessRunnerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "scriptdock-runner-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _runsDir = Path.Combine(_dir, "runs");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private string WriteExecutableScript(string name, string body)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "#!/usr/bin/env bash\n" + body);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        return path;
    }

    [MacOnlyFact]
    public void Start_WritesOutputToRunLog_AndExitsCleanly()
    {
        var script = WriteExecutableScript("hello.command", "echo hello-from-script\nexit 0\n");
        var runner = new ProcessRunner(_runsDir);

        var handle = runner.Start(script);
        Assert.True(handle.WaitForExit(TimeSpan.FromSeconds(20)));

        Assert.Equal(RunState.Exited, handle.State);
        Assert.Equal(0, handle.ExitCode);
        Assert.Contains("hello-from-script", handle.ReadOutput());
        Assert.NotNull(handle.LogFilePath);
        Assert.True(File.Exists(handle.LogFilePath!));
    }

    [MacOnlyFact]
    public void Start_AcceptsStdinInput_AndScriptReadsIt()
    {
        var script = WriteExecutableScript("echoer.command", "read line\necho \"got:$line\"\nexit 0\n");
        var runner = new ProcessRunner(_runsDir);

        var handle = runner.Start(script);
        try
        {
            Assert.True(handle.AcceptsInput);
            handle.SendInput("hello-stdin");

            Assert.True(handle.WaitForExit(TimeSpan.FromSeconds(20)));
            Assert.Contains(handle.ReadOutput(), line => line.Contains("got:hello-stdin"));
        }
        finally
        {
            runner.Terminate(handle);
        }
    }

    [MacOnlyFact]
    public void Terminate_StopsALongRunningScript()
    {
        var script = WriteExecutableScript("sleeper.command", "echo started\nsleep 60\n");
        var runner = new ProcessRunner(_runsDir);

        var handle = runner.Start(script);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!handle.ReadOutput().Contains("started") && DateTime.UtcNow < deadline)
            Thread.Sleep(50);
        Assert.Contains("started", handle.ReadOutput());

        runner.Terminate(handle);
        Assert.True(handle.WaitForExit(TimeSpan.FromSeconds(20)));
        Assert.Equal(RunState.Terminated, handle.State);
    }

    [Fact]
    public void StartTimesMatch_TrueWithinTolerance_FalseBeyond()
    {
        var t = new DateTimeOffset(2026, 6, 19, 1, 2, 3, TimeSpan.Zero);

        Assert.True(ProcessRunner.StartTimesMatch(t, t));
        Assert.True(ProcessRunner.StartTimesMatch(t, t.AddSeconds(1)));
        Assert.True(ProcessRunner.StartTimesMatch(t, t.AddSeconds(-2)));
        Assert.False(ProcessRunner.StartTimesMatch(t, t.AddSeconds(5)));
        Assert.False(ProcessRunner.StartTimesMatch(t, t.AddMinutes(1)));
    }

    [MacOnlyFact]
    public void Recapture_ReattachesRunningProcess_ByPidAndStartTime()
    {
        var script = WriteExecutableScript("sleeper.command", "echo started\nsleep 60\n");
        var runner = new ProcessRunner(_runsDir);
        var handle = runner.Start(script);

        try
        {
            // Wait until it is genuinely running with a known PID + start-time.
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while ((handle.Pid is null || handle.OsStartedAt is null) && DateTime.UtcNow < deadline)
                Thread.Sleep(50);
            Assert.NotNull(handle.Pid);
            Assert.NotNull(handle.OsStartedAt);

            var record = new PersistedProcess
            {
                Pid = handle.Pid!.Value,
                OsStartedAt = handle.OsStartedAt!.Value,
                LaunchedAt = handle.StartedAt,
                ScriptPath = handle.ScriptPath,
                LogFilePath = handle.LogFilePath ?? "",
            };

            // A fresh runner — as if the app restarted — re-attaches by PID + start-time.
            var relaunched = new ProcessRunner(_runsDir);
            relaunched.Recapture([record]);

            var recaptured = Assert.Single(relaunched.Active);
            Assert.Equal(RunState.Running, recaptured.State);
            Assert.Equal(script, recaptured.ScriptPath);

            // Reused-PID guard: same PID but a different start-time must NOT re-attach.
            var mismatched = new PersistedProcess
            {
                Pid = handle.Pid!.Value,
                OsStartedAt = handle.OsStartedAt!.Value.AddMinutes(5),
                LaunchedAt = handle.StartedAt,
                ScriptPath = handle.ScriptPath,
                LogFilePath = handle.LogFilePath ?? "",
            };
            var picky = new ProcessRunner(_runsDir);
            picky.Recapture([mismatched]);
            Assert.Empty(picky.Active);
        }
        finally
        {
            runner.Terminate(handle);
            handle.WaitForExit(TimeSpan.FromSeconds(20));
        }
    }
}
