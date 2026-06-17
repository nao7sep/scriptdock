using System;
using System.IO;
using System.Linq;
using System.Threading;
using ScriptDock.Models;
using ScriptDock.Services;
using Xunit;

namespace ScriptDock.Tests.Services;

/// <summary>
/// Integration tests for the runner: they launch real processes through the login shell,
/// so they run on macOS only. They cover the launch+capture path, a clean exit, and
/// process-tree termination of a long-running script.
/// </summary>
public sealed class ProcessRunnerTests : IDisposable
{
    private readonly string _dir;

    public ProcessRunnerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "scriptdock-runner-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
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
    public void Start_CapturesOutput_AndExitsCleanly()
    {
        var script = WriteExecutableScript("hello.command", "echo hello-from-script\nexit 0\n");
        var runner = new ProcessRunner();

        var handle = runner.Start(script);
        Assert.True(handle.WaitForExit(TimeSpan.FromSeconds(20)));

        Assert.Equal(RunState.Exited, handle.State);
        Assert.Equal(0, handle.ExitCode);
        Assert.Contains("hello-from-script", handle.Snapshot());
    }

    [MacOnlyFact]
    public void Terminate_StopsALongRunningScript()
    {
        var script = WriteExecutableScript("sleeper.command", "echo started\nsleep 60\n");
        var runner = new ProcessRunner();

        var handle = runner.Start(script);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!handle.Snapshot().Contains("started") && DateTime.UtcNow < deadline)
            Thread.Sleep(50);
        Assert.Contains("started", handle.Snapshot());

        runner.Terminate(handle);
        Assert.True(handle.WaitForExit(TimeSpan.FromSeconds(20)));
        Assert.Equal(RunState.Terminated, handle.State);
    }
}
