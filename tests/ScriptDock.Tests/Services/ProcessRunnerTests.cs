using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ScriptDock;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.Tests.Fakes;
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
        _dir = Path.Combine(Path.GetTempPath(), "scriptdock-runner-tests", NanoId.New());
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
    public void Start_CapturesNonZeroExitCode()
    {
        // The failure path a launcher most needs to surface: a non-zero exit must be captured, not
        // dropped to null. (Clean exit 0 and tree-kill Terminated are covered above/below.)
        var script = WriteExecutableScript("fail.command", "echo nope\nexit 3\n");
        var runner = new ProcessRunner(_runsDir);

        var handle = runner.Start(script);
        Assert.True(handle.WaitForExit(TimeSpan.FromSeconds(20)));

        Assert.Equal(RunState.Exited, handle.State);
        Assert.Equal(3, handle.ExitCode);
    }

    [WindowsOnlyFact]
    public void Start_WindowsPreservesScriptExitCodeAndOutput()
    {
        var script = Path.Combine(_dir, "exit-code.ps1");
        File.WriteAllText(script, "Write-Output 'windows-output'\nexit 7\n");
        var runner = new ProcessRunner(_runsDir);

        var handle = runner.Start(script);
        Assert.True(handle.WaitForExit(TimeSpan.FromSeconds(20)));

        Assert.Equal(RunState.Exited, handle.State);
        Assert.Equal(7, handle.ExitCode);
        Assert.Contains("windows-output", handle.ReadOutput());
    }

    [MacOnlyFact]
    public async Task Start_AcceptsStdinInput_AndScriptReadsIt()
    {
        var script = WriteExecutableScript("echoer.command", "read line\necho \"got:$line\"\nexit 0\n");
        var runner = new ProcessRunner(_runsDir);

        var handle = runner.Start(script);
        try
        {
            Assert.True(await handle.SendInputAsync("hello-stdin"));

            Assert.True(handle.WaitForExit(TimeSpan.FromSeconds(20)));
            Assert.Contains(handle.ReadOutput(), line => line.Contains("got:hello-stdin"));
        }
        finally
        {
            await runner.TerminateAsync(handle);
        }
    }

    [MacOnlyFact]
    public async Task SendInputAsync_ConcurrentLinesStayOrdered()
    {
        var script = WriteExecutableScript(
            "ordered.command",
            "read first\nread second\nprintf '%s|%s\\n' \"$first\" \"$second\"\n");
        var runner = new ProcessRunner(_runsDir);
        var handle = runner.Start(script);
        try
        {
            var first = handle.SendInputAsync("one");
            var second = handle.SendInputAsync("two");
            Assert.All(await Task.WhenAll(first, second), Assert.True);

            Assert.True(handle.WaitForExit(TimeSpan.FromSeconds(20)));
            Assert.Contains("one|two", handle.ReadOutput());
        }
        finally
        {
            await runner.TerminateAsync(handle);
        }
    }

    [MacOnlyFact]
    public async Task SendInputAsync_UnreadPipeIsBoundedAndCancellationPreservesFailure()
    {
        var script = WriteExecutableScript("no-read.command", "sleep 60\n");
        var runner = new ProcessRunner(_runsDir);
        var handle = runner.Start(script);
        try
        {
            var line = new string('x', 8 * 1024 * 1024);
            var stopwatch = Stopwatch.StartNew();

            Assert.False(await handle.SendInputAsync(line, TimeSpan.FromMilliseconds(100)));
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
        }
        finally
        {
            await runner.TerminateAsync(handle);
        }
    }

    [MacOnlyFact]
    public async Task Terminate_StopsALongRunningScript()
    {
        var script = WriteExecutableScript("sleeper.command", "echo started\nsleep 60\n");
        var runner = new ProcessRunner(_runsDir);

        var handle = runner.Start(script);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!handle.ReadOutput().Contains("started") && DateTime.UtcNow < deadline)
            Thread.Sleep(50);
        Assert.Contains("started", handle.ReadOutput());

        Assert.True(await runner.TerminateAsync(handle));
        Assert.Equal(RunState.Terminated, handle.State);
    }

    [MacOnlyFact]
    public async Task TerminateAsync_KillFailureKeepsInputUsableAndNaturalExitIsNotTerminated()
    {
        var script = WriteExecutableScript("kill-fails.command", "read line\necho got:$line\n");
        var runner = new ProcessRunner(
            _runsDir,
            TimeSpan.FromMilliseconds(50),
            _ => throw new InvalidOperationException("injected kill failure"));
        var handle = runner.Start(script);

        Assert.False(await runner.TerminateAsync(handle));
        Assert.True(await handle.SendInputAsync("still-owned"));
        Assert.True(handle.WaitForExit(TimeSpan.FromSeconds(20)));
        Assert.Equal(RunState.Exited, handle.State);
        Assert.Contains("got:still-owned", handle.ReadOutput());
    }

    [MacOnlyFact]
    public async Task TerminateAsync_NaturalExitDuringFailedKillIsFinalizedAsExited()
    {
        var script = WriteExecutableScript("exit-during-kill.command", "read line\n");
        var runner = new ProcessRunner(
            _runsDir,
            TimeSpan.FromMilliseconds(50),
            process =>
            {
                process.StandardInput.WriteLine("finish");
                Assert.True(process.WaitForExit(20_000));
                throw new InvalidOperationException("injected failure after natural exit");
            });
        var handle = runner.Start(script);

        Assert.False(await runner.TerminateAsync(handle));
        Assert.Equal(RunState.Exited, handle.State);
    }

    [MacOnlyFact]
    public async Task TerminateAsync_TimeoutRollsBackAttemptAndNaturalExitRemainsExited()
    {
        var script = WriteExecutableScript("kill-times-out.command", "sleep 0.3\nread line\necho got:$line\n");
        var runner = new ProcessRunner(_runsDir, TimeSpan.FromMilliseconds(20), _ => { });
        var handle = runner.Start(script);

        Assert.False(await runner.TerminateAsync(handle));
        Assert.True(await handle.SendInputAsync("after-timeout"));
        Assert.True(handle.WaitForExit(TimeSpan.FromSeconds(20)));
        Assert.Equal(RunState.Exited, handle.State);
        Assert.Contains("got:after-timeout", handle.ReadOutput());
    }

    [MacOnlyFact]
    public async Task RestartAsync_StopsTheOldRun_AndStartsAFreshOne()
    {
        var script = WriteExecutableScript("sleeper.command", "echo started\nsleep 60\n");
        var runner = new ProcessRunner(_runsDir);
        var first = runner.Start(script);

        // Wait until the first run is genuinely live before restarting it.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (first.Pid is null && DateTime.UtcNow < deadline)
            Thread.Sleep(50);
        Assert.NotNull(first.Pid);

        var second = await runner.RestartAsync(first);
        Assert.NotNull(second);
        try
        {
            Assert.NotEqual(first.Id, second.Id);                  // a genuinely new run
            Assert.Equal(RunState.Terminated, first.State);        // the old run was stopped and finalised
            Assert.DoesNotContain(first, runner.Active);           // and dismissed from the active set
            Assert.Same(second, Assert.Single(runner.Active));     // only the replacement remains
        }
        finally
        {
            await runner.TerminateAsync(second);
        }
    }

    [MacOnlyFact]
    public async Task Start_ReplacesTerminalHandleForSamePhysicalScript()
    {
        var script = WriteExecutableScript("once.command", "exit 0\n");
        var runner = new ProcessRunner(_runsDir);
        var first = runner.Start(script);
        Assert.True(first.WaitForExit(TimeSpan.FromSeconds(20)));

        var second = runner.Start(script);
        try
        {
            Assert.DoesNotContain(first, runner.Active);
            Assert.Same(second, Assert.Single(runner.Active));
        }
        finally
        {
            await runner.TerminateAsync(second);
        }
    }

    [Fact]
    public void StartTimesMatch_UsesPersistedUnixMillisecondPrecision()
    {
        var t = new DateTimeOffset(2026, 6, 19, 1, 2, 3, TimeSpan.Zero);

        Assert.True(ProcessRunner.StartTimesMatch(t, t));
        Assert.True(ProcessRunner.StartTimesMatch(t.AddTicks(1), t.AddTicks(9_999)));
        Assert.False(ProcessRunner.StartTimesMatch(t, t.AddMilliseconds(1)));
        Assert.False(ProcessRunner.StartTimesMatch(t, t.AddSeconds(-1)));
        Assert.False(ProcessRunner.StartTimesMatch(t, t.AddMinutes(1)));
    }

    [Fact]
    public void WorkingDirectoryFor_IsEmptyForBareAndRootPaths()
    {
        // A bare filename and the filesystem root have no usable parent, so the working
        // directory resolves to "" — Process then inherits ScriptDock's current directory.
        Assert.Equal("", ProcessRunner.WorkingDirectoryFor("run.command"));
        Assert.Equal("", ProcessRunner.WorkingDirectoryFor("/"));
        // A path with a containing folder yields that folder (non-empty).
        Assert.NotEqual("", ProcessRunner.WorkingDirectoryFor("/proj/scripts/run.command"));
    }

    [MacOnlyFact]
    public void RunEnded_IsRaisedOnceWhenARunEnds()
    {
        var script = WriteExecutableScript("ends.command", "exit 3\n");
        var runner = new ProcessRunner(_runsDir);
        var ended = new List<ScriptProcess>();
        using var raised = new ManualResetEventSlim();
        runner.RunEnded += (_, process) => { lock (ended) ended.Add(process); raised.Set(); };

        var handle = runner.Start(script);
        Assert.True(handle.WaitForExit(TimeSpan.FromSeconds(20)));
        // RunEnded may be raised on the process's exit thread, after the wait has returned.
        Assert.True(raised.Wait(TimeSpan.FromSeconds(20)));
        handle.Complete();

        lock (ended)
            Assert.Equal([handle], ended);
        Assert.Equal(3, RunEnd.For("s", handle).ExitCode);
    }

    private string QuietOutput(string name, string content)
    {
        Directory.CreateDirectory(_runsDir);
        var path = Path.Combine(_runsDir, name);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - ProcessRunner.OutputQuietPeriod - TimeSpan.FromMinutes(1));
        return path;
    }

    private static RunRecord RecordedRun(string outputPath, int run, int? pid = null, DateTimeOffset? osStartedAt = null) =>
        new("2026-01-01T00:00:00.000Z", run, DateTimeOffset.UtcNow, "/x/a.command", pid, osStartedAt, outputPath);

    [Fact]
    public async Task ImportFinishedOutput_MovesAFinishedRunsOutputIntoTheRecords_AndDeletesTheFile()
    {
        var path = QuietOutput("finished.log", "done\n");
        var records = new FakeRecordStore();
        records.Runs.Add(RecordedRun(path, run: 4));

        await new ProcessRunner(_runsDir).ImportFinishedOutputAsync(records, CancellationToken.None);

        Assert.Equal("done\n"u8.ToArray(), records.Outputs[("2026-01-01T00:00:00.000Z", 4)]);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task ImportFinishedOutput_LeavesAFileNoRunRecordNames()
    {
        var path = QuietOutput("unrecorded.log", "old\n");
        var records = new FakeRecordStore();

        await new ProcessRunner(_runsDir).ImportFinishedOutputAsync(records, CancellationToken.None);

        Assert.Empty(records.Outputs);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task ImportFinishedOutput_LeavesAFileThatChangedRecently()
    {
        Directory.CreateDirectory(_runsDir);
        var path = Path.Combine(_runsDir, "recent.log");
        File.WriteAllText(path, "still writing\n");
        var records = new FakeRecordStore();
        records.Runs.Add(RecordedRun(path, run: 1));

        await new ProcessRunner(_runsDir).ImportFinishedOutputAsync(records, CancellationToken.None);

        Assert.Empty(records.Outputs);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task ImportFinishedOutput_LeavesTheOutputOfARunWhoseProcessIsAlive()
    {
        using var self = Process.GetCurrentProcess();
        var path = QuietOutput("alive.log", "serving\n");
        var records = new FakeRecordStore();
        records.Runs.Add(RecordedRun(path, run: 1, self.Id, new DateTimeOffset(self.StartTime).ToUniversalTime()));

        await new ProcessRunner(_runsDir).ImportFinishedOutputAsync(records, CancellationToken.None);

        Assert.Empty(records.Outputs);
        Assert.True(File.Exists(path));
    }

    [MacOnlyFact]
    public async Task ImportFinishedOutput_WaitsUntilTheRunLeavesTheActiveSet()
    {
        var script = WriteExecutableScript("brief.command", "echo brief\nexit 0\n");
        var runner = new ProcessRunner(_runsDir);
        var records = new FakeRecordStore();
        var handle = runner.Start(script);
        Assert.True(handle.WaitForExit(TimeSpan.FromSeconds(20)));
        records.Runs.Add(RunRecord.For(records.Session, handle));
        File.SetLastWriteTimeUtc(handle.LogFilePath!, DateTime.UtcNow - ProcessRunner.OutputQuietPeriod - TimeSpan.FromMinutes(1));

        await runner.ImportFinishedOutputAsync(records, CancellationToken.None);
        Assert.Empty(records.Outputs); // the console still reads it

        runner.Dismiss(handle);
        await runner.ImportFinishedOutputAsync(records, CancellationToken.None);
        Assert.Contains("brief", System.Text.Encoding.UTF8.GetString(records.Outputs[(records.Session, handle.Id)]));
        Assert.False(File.Exists(handle.LogFilePath!));
    }
}
