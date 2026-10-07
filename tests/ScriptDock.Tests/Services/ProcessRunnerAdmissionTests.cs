using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ScriptDock;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.ViewModels;
using Xunit;

namespace ScriptDock.Tests.Services;

public sealed class ProcessRunnerAdmissionTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task StopAllCapturesAnAdmittedNativeLaunchAndSealsNewLaunches()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var launches = 0;
        var existingStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new ProcessRunner(null, Bound, _ => { }, handle =>
        {
            if (Interlocked.Increment(ref launches) == 1)
                return;
            entered.SetResult();
            if (!release.Wait(Bound, TestContext.Current.CancellationToken))
                throw new TimeoutException("test launch was not released");
        }, handle =>
        {
            handle.Complete();
            if (handle.ScriptPath == "/scripts/existing.command")
                existingStopped.TrySetResult();
            return Task.FromResult(true);
        });
        var existing = Assert.IsType<ScriptProcess>(await runner.StartAsync("/scripts/existing.command"));
        var launch = runner.StartAsync("/scripts/a.command");
        Task stop = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);
            stop = runner.StopAllAsync();
            Assert.False(stop.IsCompleted);
            await existingStopped.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);
            Assert.Equal(RunState.Exited, existing.State);
            Assert.Null(await runner.StartAsync("/scripts/b.command"));
            release.Set();
            var handle = await launch.WaitAsync(Bound, TestContext.Current.CancellationToken);
            await stop.WaitAsync(Bound, TestContext.Current.CancellationToken);
            Assert.NotNull(handle);
            Assert.Equal(RunState.Exited, handle.State);
            Assert.Equal(2, launches);
            Assert.Null(await runner.StartAsync("/scripts/c.command"));
        }
        finally
        {
            release.Set();
            await launch.WaitAsync(Bound, TestContext.Current.CancellationToken);
            await stop.WaitAsync(Bound, TestContext.Current.CancellationToken);
            foreach (var handle in runner.Active) runner.Dismiss(handle);
        }
    }

    [Fact]
    public async Task PendingRestartSharesTerminationAndCannotRelaunchAfterStopAll()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var launches = 0;
        var terminations = 0;
        var runner = new ProcessRunner(null, Bound, _ => { }, _ => Interlocked.Increment(ref launches),
            async handle =>
            {
                Interlocked.Increment(ref terminations);
                entered.SetResult();
                await release.Task;
                handle.Complete();
                return true;
            });
        var handle = Assert.IsType<ScriptProcess>(await runner.StartAsync("/scripts/a.command"));
        var restart = runner.RestartAsync(handle);
        Task stop = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);
            Assert.Null(await runner.RestartAsync(handle));
            Assert.Null(await runner.StartAsync(handle.ScriptPath));
            stop = runner.StopAllAsync();
            Assert.False(stop.IsCompleted);
            release.SetResult(true);
            Assert.Null(await restart.WaitAsync(Bound, TestContext.Current.CancellationToken));
            await stop.WaitAsync(Bound, TestContext.Current.CancellationToken);
            Assert.Equal(1, launches);
            Assert.Equal(1, terminations);
            Assert.Equal(RunState.Exited, handle.State);
        }
        finally
        {
            release.TrySetResult(true);
            await restart.WaitAsync(Bound, TestContext.Current.CancellationToken);
            await stop.WaitAsync(Bound, TestContext.Current.CancellationToken);
            foreach (var owned in runner.Active) runner.Dismiss(owned);
        }
    }

    [MacOnlyFact]
    public async Task AnOwnedRunKeepsItsPhysicalIdentityAfterItsAncestorAliasDisappears()
    {
        var root = Path.Combine(Path.GetTempPath(), "scriptdock-admission-" + Guid.NewGuid());
        var physical = Path.Combine(root, "physical");
        var alias = Path.Combine(root, "alias");
        Directory.CreateDirectory(physical);
        var path = Path.Combine(physical, "run.command");
        File.WriteAllText(path, "# fixture");
        Directory.CreateSymbolicLink(alias, physical);
        var spelling = Path.Combine(alias, "run.command");
        var runner = new ProcessRunner(null, Bound, _ => { }, _ => { });
        try
        {
            var handle = Assert.IsType<ScriptProcess>(await runner.StartAsync(spelling));
            File.Delete(alias);
            Assert.False(PathIdentity.Same(spelling, path));
            Assert.Equal(PathIdentity.Key(path), handle.ScriptKey);
            Assert.Null(await runner.StartAsync(path));
            var rows = RecentListBuilder.Build(
                [new RecentRun { Path = spelling, RanAt = handle.StartedAt },
                 new RecentRun { Path = path, RanAt = handle.StartedAt }],
                runner.Active, new System.Collections.Generic.Dictionary<string, string>());
            Assert.Same(handle, Assert.Single(rows).Process);
            Assert.Equal(spelling, handle.ScriptPath);
        }
        finally
        {
            foreach (var handle in runner.Active) runner.Dismiss(handle);
            Directory.Delete(root, true);
        }
    }
}
