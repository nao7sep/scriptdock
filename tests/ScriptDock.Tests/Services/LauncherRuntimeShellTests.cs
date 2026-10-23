using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace ScriptDock.Tests.Services;

/// <summary>Launcher helpers act only on registrations for fixture processes, under a disposable
/// checkout-shaped directory. No test launches the app or writes the repository's actual .git.</summary>
public sealed class LauncherRuntimeShellTests
{
    [MacOnlyFact]
    public Task Mac_replaces_registered_process_and_accepts_activation() => ExerciseOwnership(windows: false);

    [WindowsOnlyFact]
    public Task Windows_replaces_registered_process_and_accepts_activation() => ExerciseOwnership(windows: true);

    private static async Task ExerciseOwnership(bool windows)
    {
        var directory = Path.Combine(Path.GetTempPath(), "scriptdock-launcher-shell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, ".git"));
        var registration = Path.Combine(directory, ".git", "launcher-runtime-process");
        var fixtureStart = new ProcessStartInfo(windows ? "powershell.exe" : "/bin/sleep") { UseShellExecute = false };
        if (windows)
        {
            fixtureStart.ArgumentList.Add("-NoProfile");
            fixtureStart.ArgumentList.Add("-Command");
            fixtureStart.ArgumentList.Add("Start-Sleep -Seconds 60");
        }
        else fixtureStart.ArgumentList.Add("60");
        using var fixture = Process.Start(fixtureStart)!;
        try
        {
            var started = new DateTimeOffset(fixture.StartTime.ToUniversalTime()).ToUnixTimeSeconds();
            void Register(string token, int processId, long start) => File.WriteAllText(registration,
                token + "\n" + processId.ToString(CultureInfo.InvariantCulture) + "\n" + start.ToString(CultureInfo.InvariantCulture) + "\n");

            // A reused PID has a different start identity: neither replacement nor cleanup may kill it.
            Register("previous", fixture.Id, started - 1);
            await RunHelper(windows, directory, "claim-stop");
            Assert.False(fixture.HasExited);

            // Cleanup from an interrupted invocation cannot claim the next invocation's process.
            Register("newer", fixture.Id, started);
            await RunHelper(windows, directory, "cleanup");
            Assert.False(fixture.HasExited);

            // A non-owner launcher also leaves the registered app alone.
            File.WriteAllText(Path.Combine(directory, ".git", "launcher-runtime-owner"), "newer\n");
            await RunHelper(windows, directory, "stop");
            Assert.False(fixture.HasExited);

            Register("current", fixture.Id, started);
            await RunHelper(windows, directory, "wait");

            Register("previous", fixture.Id, started);
            await RunHelper(windows, directory, "claim-stop");
            await fixture.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

            // Activation acknowledges startup but grants no authority over the independently run app.
            Register("current", 0, 0);
            await RunHelper(windows, directory, "wait");
        }
        finally
        {
            if (!fixture.HasExited) fixture.Kill(entireProcessTree: true);
            await fixture.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task RunHelper(bool windows, string directory, string action)
    {
        var start = new ProcessStartInfo(windows ? "powershell.exe" : "/bin/bash")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (windows)
        {
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-Command");
            // Environment values pass fixture paths as data, including spaces and apostrophes.
            start.Environment["SCRIPTDOCK_TEST_HELPER"] = SourceScan.RepoFile("scripts", "launcher-runtime.psm1");
            start.Environment["SCRIPTDOCK_TEST_REPO"] = directory;
            start.Environment["SCRIPTDOCK_TEST_ACTION"] = action;
            start.ArgumentList.Add("""
                $ErrorActionPreference = 'Stop'
                Import-Module $env:SCRIPTDOCK_TEST_HELPER -Force
                $repo = $env:SCRIPTDOCK_TEST_REPO
                switch ($env:SCRIPTDOCK_TEST_ACTION) {
                    'claim-stop' { Claim-LauncherRuntime current $repo; Stop-OwnedRuntime fixture $repo current }
                    'cleanup' { Stop-OwnedRuntime fixture $repo current -RequireCurrentRegistration }
                    'stop' { Stop-OwnedRuntime fixture $repo current }
                    'wait' { Claim-LauncherRuntime current $repo; Wait-OwnedRuntime fixture $repo current -TimeoutSeconds 2 }
                }
                """);
        }
        else
        {
            start.Environment.Remove("BASH_ENV");
            start.Environment.Remove("ENV");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("""
                set -euo pipefail
                source "$1"
                case "$3" in
                  claim-stop) claim_launcher_runtime current "$2"; stop_owned_runtime fixture "$2" current ;;
                  cleanup) stop_owned_runtime fixture "$2" current true ;;
                  stop) stop_owned_runtime fixture "$2" current ;;
                  wait) claim_launcher_runtime current "$2"; wait_for_owned_runtime fixture "$2" current 2 ;;
                esac
                """);
            start.ArgumentList.Add("launcher-test");
            start.ArgumentList.Add(SourceScan.RepoFile("scripts", "launcher-runtime.sh"));
            start.ArgumentList.Add(directory);
            start.ArgumentList.Add(action);
        }
        using var helper = Process.Start(start)!;
        var output = helper.StandardOutput.ReadToEndAsync();
        var errors = helper.StandardError.ReadToEndAsync();
        try
        {
            await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(helper.ExitCode == 0, (await output) + (await errors));
        }
        finally
        {
            if (!helper.HasExited) helper.Kill(entireProcessTree: true);
            await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
