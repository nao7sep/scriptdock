using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ScriptDock.Models;
using ScriptDock.Services;
using Xunit;

namespace ScriptDock.Tests.Services;

/// <summary>
/// Real scripts through each platform's shell: zsh as a login shell on macOS, pwsh on Windows
/// (<see cref="ShellCommand"/>). The cross-platform facts run the same behavior on whichever system runs
/// the suite, each with a script written for that system's shell; the platform facts cover what only one
/// shell does. Windows-only facts skip visibly elsewhere and run in the fleet Windows run.
/// </summary>
public sealed class ProcessPlatformTests : IAsyncLifetime
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);
    private readonly ProcessFixture _fixture = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    // A script for the running system: a .command for zsh, or a .ps1 for pwsh, in folder (by default the fixture's).
    private string Script(string name, string unix, string windows, string? folder = null)
    {
        var directory = Directory.CreateDirectory(folder ?? _fixture.Root).FullName;
        if (OperatingSystem.IsWindows())
        {
            var ps1 = Path.Combine(directory, name + ".ps1");
            File.WriteAllText(ps1, windows);
            return ps1;
        }

        var command = Path.Combine(directory, name + ".command");
        File.WriteAllText(command, "#!/bin/zsh\n" + unix);
        File.SetUnixFileMode(command, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return command;
    }

    private static async Task<ScriptProcess> StartAsync(ProcessRunner runner, string path) =>
        Assert.IsType<ScriptProcess>(await runner.StartAsync(path));

    private static async Task<IReadOnlyList<string>> RunToEndAsync(ProcessRunner runner, string path)
    {
        var handle = await StartAsync(runner, path);
        Assert.True(await handle.WaitForExitAsync(Bound));
        return handle.ReadOutput();
    }

    private static void WaitForLine(ScriptProcess handle, string line)
    {
        var deadline = DateTime.UtcNow + Bound;
        while (!handle.ReadOutput().Contains(line) && DateTime.UtcNow < deadline)
            Thread.Sleep(50);
        Assert.Contains(line, handle.ReadOutput());
    }

    // ── Both platforms ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_script_runs_in_its_own_folder()
    {
        var folder = Path.Combine(_fixture.Root, "work");
        var script = Script("where", "pwd -P\n", "(Get-Location).ProviderPath\n", folder);

        var output = await RunToEndAsync(_fixture.Runner(), script);

        // The same folder by identity: the temporary folder can be reached through a link (/var is /private/var).
        Assert.Contains(output, line => line.Length > 0 && PathIdentity.Comparer.Equals(PathIdentity.Key(line), PathIdentity.Key(folder)));
    }

    // The path reaches the shell as one quoted argument: spaces, a single quote, a dollar sign and
    // non-ASCII names are taken literally, never split or expanded.
    [Fact]
    public async Task A_path_with_spaces_a_quote_a_dollar_and_Japanese_runs_as_written()
    {
        var folder = Path.Combine(_fixture.Root, "it's $HOME 日本語 scripts");
        var script = Script("実行 me", "echo ran-literal-path\n", "Write-Output 'ran-literal-path'\n", folder);

        var output = await RunToEndAsync(_fixture.Runner(), script);

        Assert.Contains("ran-literal-path", output);
    }

    [Fact]
    public async Task Output_and_errors_both_reach_the_run_log_and_non_ASCII_text_stays_intact()
    {
        var script = Script("streams",
            "echo '出力 ✓ out'\necho '错误 err' >&2\n",
            "Write-Output '出力 ✓ out'\n[Console]::Error.WriteLine('错误 err')\n");

        var output = await RunToEndAsync(_fixture.Runner(), script);

        Assert.Contains("出力 ✓ out", output);
        Assert.Contains("错误 err", output);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task The_scripts_exit_code_is_the_runs(int code)
    {
        var script = Script("exits-" + code, $"exit {code}\n", $"exit {code}\n");
        var handle = await StartAsync(_fixture.Runner(), script);

        Assert.True(await handle.WaitForExitAsync(Bound));
        Assert.Equal(RunState.Exited, handle.State);
        Assert.Equal(code, handle.ExitCode);
    }

    [Fact]
    public async Task Lines_typed_into_the_console_reach_the_script_in_order()
    {
        var script = Script("reader",
            "read a\nread b\nread c\necho \"got:$a,$b,$c\"\n",
            "$a = [Console]::In.ReadLine(); $b = [Console]::In.ReadLine(); $c = [Console]::In.ReadLine()\nWrite-Output \"got:$a,$b,$c\"\n");
        var runner = _fixture.Runner();
        var handle = await StartAsync(runner, script);
        try
        {
            Assert.True(await handle.SendInputAsync("one"));
            Assert.True(await handle.SendInputAsync("二"));
            Assert.True(await handle.SendInputAsync("three"));
            Assert.True(await handle.WaitForExitAsync(Bound));
            Assert.Contains("got:one,二,three", handle.ReadOutput());
        }
        finally { await runner.TerminateAsync(handle); }
    }

    [Fact]
    public async Task Stop_ends_a_long_running_script()
    {
        var script = Script("sleeper", "echo started\nsleep 300\n", "Write-Output 'started'\nStart-Sleep 300\n");
        var runner = _fixture.Runner();
        var handle = await StartAsync(runner, script);
        try
        {
            WaitForLine(handle, "started");
            Assert.True(await runner.TerminateAsync(handle));
            Assert.Equal(RunState.Terminated, handle.State);
        }
        finally { await runner.TerminateAsync(handle); }
    }

    [Fact]
    public async Task Restart_stops_the_old_run_and_starts_a_fresh_one()
    {
        var script = Script("restartee", "echo started\nsleep 300\n", "Write-Output 'started'\nStart-Sleep 300\n");
        var runner = _fixture.Runner();
        var first = await StartAsync(runner, script);
        WaitForLine(first, "started");

        var second = await runner.RestartAsync(first);
        Assert.NotNull(second);
        try
        {
            Assert.Equal(RunState.Terminated, first.State);
            Assert.Same(second, Assert.Single(runner.Active));
            WaitForLine(second!, "started");
        }
        finally { await runner.TerminateAsync(second!); }
    }

    [Fact]
    public async Task Quitting_stops_every_running_script_each_ending_once()
    {
        var runner = _fixture.Runner();
        var ended = new List<ScriptProcess>();
        runner.RunEnded += (_, process) => { lock (ended) ended.Add(process); };
        var a = await StartAsync(runner, Script("first", "echo started\nsleep 300\n", "Write-Output 'started'\nStart-Sleep 300\n"));
        var b = await StartAsync(runner, Script("second", "echo started\nsleep 300\n", "Write-Output 'started'\nStart-Sleep 300\n"));
        WaitForLine(a, "started");
        WaitForLine(b, "started");

        await runner.StopAllAsync();

        Assert.Equal(RunState.Terminated, a.State);
        Assert.Equal(RunState.Terminated, b.State);
        var deadline = DateTime.UtcNow + Bound;
        while (DateTime.UtcNow < deadline && ended.Count < 2)
            Thread.Sleep(50);
        lock (ended)
            Assert.Equal(new[] { a.Id, b.Id }.Order(), ended.Select(process => process.Id).Order());
    }

    // A computer without the shell (pwsh missing on Windows) fails the launch, which the window reports,
    // and leaves no run behind.
    [Fact]
    public async Task A_missing_shell_fails_the_launch_and_leaves_no_run()
    {
        var script = Script("unreached", "echo unreached\n", "Write-Output 'unreached'\n");
        var runner = _fixture.Runner(configure: start => start.FileName = "scriptdock-no-such-shell");

        await Assert.ThrowsAsync<Win32Exception>(() => runner.StartAsync(script));

        Assert.Empty(runner.Active);
        Assert.IsType<ScriptProcess>(await _fixture.Runner().StartAsync(script)); // the script itself still runs
    }

    // ── macOS: zsh as a login shell ─────────────────────────────────────────────────────────────

    // Scripts see the PATH and variables the user's terminal does: zsh runs as a login shell, so it
    // reads .zprofile; it is not interactive, so .zshrc is not read.
    [MacOnlyFact]
    public async Task On_macOS_a_script_sees_the_login_shell_profile()
    {
        File.WriteAllText(Path.Combine(_fixture.ShellHome, ".zprofile"), "export SCRIPTDOCK_PROFILE=from-zprofile\n");
        File.WriteAllText(Path.Combine(_fixture.ShellHome, ".zshrc"), "export SCRIPTDOCK_RC=from-zshrc\n");
        var script = Script("profile", "echo \"profile:$SCRIPTDOCK_PROFILE rc:$SCRIPTDOCK_RC\"\n", "");

        var output = await RunToEndAsync(_fixture.Runner(), script);

        Assert.Contains("profile:from-zprofile rc:", output);
    }

    // A script without its execute bit is the commonest first failure on a Mac: the run fails with the
    // shell's own exit code and message, both of which the user sees.
    [MacOnlyFact]
    public async Task On_macOS_a_script_that_is_not_executable_fails_with_the_shells_message()
    {
        if (OperatingSystem.IsWindows()) return; // MacOnlyFact skips Windows; this is the guard the platform analyzer reads
        var script = Script("not-executable", "echo never\n", "");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var handle = await StartAsync(_fixture.Runner(), script);

        Assert.True(await handle.WaitForExitAsync(Bound));
        Assert.Equal(126, handle.ExitCode);
        Assert.Contains(handle.ReadOutput(), line => line.Contains("permission denied", StringComparison.OrdinalIgnoreCase));
    }

    // A script with no #! line still runs: zsh runs it as a shell script.
    [MacOnlyFact]
    public async Task On_macOS_a_script_without_a_shebang_runs()
    {
        if (OperatingSystem.IsWindows()) return; // MacOnlyFact skips Windows; this is the guard the platform analyzer reads
        var script = Path.Combine(_fixture.Root, "plain.sh");
        File.WriteAllText(script, "echo ran-without-shebang\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var output = await RunToEndAsync(_fixture.Runner(), script);

        Assert.Contains("ran-without-shebang", output);
    }

    // A script killed by a signal is a failure, not a clean exit: zsh reports 128 plus the signal.
    [MacOnlyFact]
    public async Task On_macOS_a_script_killed_by_a_signal_reports_a_failure_code()
    {
        var script = Script("killed", "kill -9 $$\n", "");
        var handle = await StartAsync(_fixture.Runner(), script);

        Assert.True(await handle.WaitForExitAsync(Bound));
        Assert.Equal(128 + 9, handle.ExitCode);
    }

    // ── Windows: pwsh ───────────────────────────────────────────────────────────────────────────

    [WindowsOnlyFact]
    public async Task On_Windows_a_cmd_script_runs_with_its_exit_code()
    {
        var script = Path.Combine(_fixture.Root, "exits.cmd");
        File.WriteAllText(script, "@echo off\r\necho cmd-ran\r\nexit /b 4\r\n");
        var handle = await StartAsync(_fixture.Runner(), script);

        Assert.True(await handle.WaitForExitAsync(Bound));
        Assert.Equal(4, handle.ExitCode);
        Assert.Contains("cmd-ran", handle.ReadOutput());
    }

    // Every PowerShell stream reaches the log, not only output: errors and warnings too.
    [WindowsOnlyFact]
    public async Task On_Windows_errors_and_warnings_reach_the_run_log()
    {
        var script = Path.Combine(_fixture.Root, "streams.ps1");
        File.WriteAllText(script, "Write-Warning 'warned-line'\nWrite-Error 'errored-line'\nWrite-Output 'output-line'\n");

        var output = await RunToEndAsync(_fixture.Runner(), script);

        Assert.Contains(output, line => line.Contains("warned-line"));
        Assert.Contains(output, line => line.Contains("errored-line"));
        Assert.Contains("output-line", output);
    }
}
