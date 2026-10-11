using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using ScriptDock.Services;

namespace ScriptDock.Tests.Services;

// Owns real fixture processes even when a body fails before reaching its local finally or exercises
// a deliberately broken kill adapter. Shell startup is fixture data, independent of personal profiles.
internal sealed class ProcessFixture : IAsyncDisposable
{
    private readonly List<ProcessRunner> _runners = [];
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "scriptdock-process-" + Guid.NewGuid().ToString("N"));
    public string Runs => Path.Combine(Root, "runs");
    public string ShellHome => Path.Combine(Root, "shell-home");

    public ProcessFixture() => Directory.CreateDirectory(ShellHome);

    public ProcessRunner Runner(TimeSpan? grace = null, Action<Process>? kill = null)
    {
        var runner = new ProcessRunner(Runs, grace ?? TimeSpan.FromSeconds(10),
            kill ?? (process => process.Kill(entireProcessTree: true)), configureStartInfo: start =>
            {
                start.Environment["HOME"] = ShellHome;
                start.Environment["ZDOTDIR"] = ShellHome;
                start.Environment.Remove("BASH_ENV");
                start.Environment.Remove("ENV");
                if (OperatingSystem.IsWindows()) start.ArgumentList.Insert(0, "-NoProfile");
            });
        _runners.Add(runner);
        return runner;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var runner in _runners) runner.SealLaunches();
        List<Exception> failures = [];
        foreach (var runner in _runners)
        {
            foreach (var handle in runner.Active)
            {
                try
                {
                    if (handle.State != ScriptDock.Models.RunState.Running)
                    {
                        handle.Dispose();
                        continue;
                    }
                    if (handle.Process is { } process && !process.HasExited)
                        process.Kill(entireProcessTree: true);
                    if (!await handle.WaitForExitAsync(TimeSpan.FromSeconds(20)))
                        throw new InvalidOperationException("Fixture process did not stop.");
                    handle.Dispose();
                }
                catch (Exception ex) { failures.Add(ex); }
            }
            try { await runner.StopAllAsync(); }
            catch (Exception ex) { failures.Add(ex); }
        }
        if (failures.Count > 0) throw new AggregateException(failures);
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
    }
}
