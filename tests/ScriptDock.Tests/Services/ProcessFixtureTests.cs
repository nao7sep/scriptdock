using System;
using System.Threading;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using ScriptDock.Models;
using Xunit;

namespace ScriptDock.Tests.Services;

public sealed class ProcessFixtureTests
{
    private static string Script(ProcessFixture fixture, string body)
    {
        var path = Path.Combine(fixture.Root, "fixture.command");
        File.WriteAllText(path, "#!/bin/bash\n" + body);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [MacOnlyFact]
    public async Task FailedBodyStillStopsTheAdmittedProcessWithABrokenKillAdapter()
    {
        var fixture = new ProcessFixture();
        var runner = fixture.Runner(TimeSpan.FromMilliseconds(20), _ => throw new IOException("injected kill failure"));
        var path = Script(fixture, "read line\n");
        var pid = 0;
        try
        {
            await Assert.ThrowsAsync<IOException>(async () =>
            {
                var handle = Assert.IsType<ScriptDock.Services.ScriptProcess>(await runner.StartAsync(path));
                pid = Assert.IsType<int>(handle.Pid);
                throw new IOException("failed immediately after admission");
            });
        }
        finally { await fixture.DisposeAsync(); }
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
        Assert.False(Directory.Exists(fixture.Root));
    }

    [MacOnlyFact]
    public async Task RunnerUsesDisposableHomeAndCannotExecuteInheritedStartupScripts()
    {
        await using var fixture = new ProcessFixture();
        var inherited = Directory.CreateDirectory(Path.Combine(fixture.Root, "inherited-profile")).FullName;
        var marker = Path.Combine(inherited, "executed");
        File.WriteAllText(Path.Combine(inherited, ".zprofile"), $"touch '{marker}'\n");
        File.WriteAllText(Path.Combine(inherited, ".zshenv"), $"touch '{marker}'\n");
        var bashEnv = Path.Combine(inherited, "bash-env");
        File.WriteAllText(bashEnv, $"touch '{marker}'\n");
        var oldZdotdir = Environment.GetEnvironmentVariable("ZDOTDIR");
        var oldBashEnv = Environment.GetEnvironmentVariable("BASH_ENV");
        try
        {
            Environment.SetEnvironmentVariable("ZDOTDIR", inherited);
            Environment.SetEnvironmentVariable("BASH_ENV", bashEnv);
            var runner = fixture.Runner();
            var handle = Assert.IsType<ScriptDock.Services.ScriptProcess>(await runner.StartAsync(Script(fixture, "echo \"$HOME\"\n")));
            Assert.True(await handle.WaitForExitAsync(Timeout.InfiniteTimeSpan));
            Assert.Equal(RunState.Exited, handle.State);
            Assert.False(File.Exists(marker));
            Assert.Contains(fixture.ShellHome, handle.ReadConsoleOutput());
        }
        finally
        {
            try { await fixture.DisposeAsync(); }
            finally
            {
                Environment.SetEnvironmentVariable("ZDOTDIR", oldZdotdir);
                Environment.SetEnvironmentVariable("BASH_ENV", oldBashEnv);
            }
        }
    }
}
