using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.Tests.Fakes;
using ScriptDock.ViewModels;
using Xunit;

namespace ScriptDock.Tests.ViewModels;

/// <summary>
/// Orchestration tests for the destructive-action confirm gating and the after-event selection,
/// driven through a <see cref="FakeProcessRunner"/> so no real processes are launched.
/// </summary>
public sealed class MainWindowViewModelTests
{
    private static (MainWindowViewModel vm, FakeProcessRunner runner) BuildVm(
        AppConfig? config = null, AppState? state = null)
    {
        config ??= new AppConfig();
        state ??= new AppState();
        var configStore = new FakeJsonStore<AppConfig> { Value = config };
        var stateStore = new FakeJsonStore<AppState> { Value = state };
        var runner = new FakeProcessRunner();
        var vm = new MainWindowViewModel(configStore, stateStore, config, state, new ScriptScanner(), runner);
        return (vm, runner);
    }

    // Records the requests it is asked and returns a fixed verdict.
    private sealed class ConfirmSpy
    {
        private readonly bool _result;
        public ConfirmSpy(bool result) => _result = result;
        public List<ConfirmRequest> Requests { get; } = new();
        public Task<bool> Handle(ConfirmRequest request)
        {
            Requests.Add(request);
            return Task.FromResult(_result);
        }
    }

    [Fact]
    public async Task StopEntry_RunningEntry_DeclinedConfirm_DoesNotTerminate()
    {
        var (vm, runner) = BuildVm();
        var process = runner.AddRunning("/x/run.command");
        var entry = new DockEntry("/x/run.command", "run.command", DateTimeOffset.UtcNow, process);
        var confirm = new ConfirmSpy(result: false);
        vm.ConfirmHandler = confirm.Handle;

        await vm.StopEntryCommand.ExecuteAsync(entry);

        Assert.Single(confirm.Requests);          // it asked
        Assert.Empty(runner.TerminateCalls);      // and respected the "no"
    }

    [Fact]
    public async Task StopEntry_RunningEntry_AcceptedConfirm_Terminates()
    {
        var (vm, runner) = BuildVm();
        var process = runner.AddRunning("/x/run.command");
        var entry = new DockEntry("/x/run.command", "run.command", DateTimeOffset.UtcNow, process);
        vm.ConfirmHandler = new ConfirmSpy(result: true).Handle;

        await vm.StopEntryCommand.ExecuteAsync(entry);

        Assert.Same(process, Assert.Single(runner.TerminateCalls));
    }

    [Fact]
    public async Task DismissEntry_FinishedEntry_DoesNotConfirm_AndRemoves()
    {
        var state = new AppState { RecentlyRun = [new RecentRun { Path = "/x/done.command", RanAt = DateTimeOffset.UtcNow }] };
        var (vm, _) = BuildVm(state: state);
        var entry = new DockEntry("/x/done.command", "done.command", DateTimeOffset.UtcNow, process: null);
        var confirm = new ConfirmSpy(result: false); // would block if consulted
        vm.ConfirmHandler = confirm.Handle;

        await vm.DismissEntryCommand.ExecuteAsync(entry);

        Assert.Empty(confirm.Requests); // a finished entry is reversible — no prompt
        Assert.DoesNotContain(state.RecentlyRun, r => r.Path == "/x/done.command");
    }

    [Fact]
    public async Task DismissEntry_RunningEntry_DeclinedConfirm_KeepsItAndDoesNotTerminate()
    {
        var state = new AppState { RecentlyRun = [new RecentRun { Path = "/x/live.command", RanAt = DateTimeOffset.UtcNow }] };
        var (vm, runner) = BuildVm(state: state);
        var process = runner.AddRunning("/x/live.command");
        var entry = new DockEntry("/x/live.command", "live.command", DateTimeOffset.UtcNow, process);
        vm.ConfirmHandler = new ConfirmSpy(result: false).Handle;

        await vm.DismissEntryCommand.ExecuteAsync(entry);

        Assert.Empty(runner.TerminateCalls);
        Assert.Empty(runner.DismissCalls);
        Assert.Contains(state.RecentlyRun, r => r.Path == "/x/live.command");
    }

    [Fact]
    public async Task DismissEntry_RunningEntry_AcceptedConfirm_TerminatesAndRemoves()
    {
        var state = new AppState { RecentlyRun = [new RecentRun { Path = "/x/live.command", RanAt = DateTimeOffset.UtcNow }] };
        var (vm, runner) = BuildVm(state: state);
        var process = runner.AddRunning("/x/live.command");
        var entry = new DockEntry("/x/live.command", "live.command", DateTimeOffset.UtcNow, process);
        vm.ConfirmHandler = new ConfirmSpy(result: true).Handle;

        await vm.DismissEntryCommand.ExecuteAsync(entry);

        Assert.Same(process, Assert.Single(runner.TerminateCalls));
        Assert.Same(process, Assert.Single(runner.DismissCalls));
        Assert.DoesNotContain(state.RecentlyRun, r => r.Path == "/x/live.command");
    }

    [Fact]
    public async Task RunScript_AlreadyRunning_DeclinedConfirm_DoesNotRestart()
    {
        var (vm, runner) = BuildVm();
        runner.AddRunning("/x/dev.command"); // already running → Run would restart
        var item = new ScriptItem("/x/dev.command") { DisplayName = "dev.command" };
        vm.ConfirmHandler = new ConfirmSpy(result: false).Handle;

        await vm.RunScriptCommand.ExecuteAsync(item);

        Assert.Empty(runner.RestartCalls);
        Assert.Empty(runner.StartCalls);
    }

    [Fact]
    public async Task RunScript_AlreadyRunning_AcceptedConfirm_Restarts()
    {
        var (vm, runner) = BuildVm();
        var process = runner.AddRunning("/x/dev.command");
        var item = new ScriptItem("/x/dev.command") { DisplayName = "dev.command" };
        vm.ConfirmHandler = new ConfirmSpy(result: true).Handle;

        await vm.RunScriptCommand.ExecuteAsync(item);

        Assert.Same(process, Assert.Single(runner.RestartCalls));
    }

    [Fact]
    public async Task RunScript_NotRunning_StartsWithoutConfirming_AndSelectsRecentEntry()
    {
        var (vm, runner) = BuildVm();
        var item = new ScriptItem("/x/new.command") { DisplayName = "new.command" };
        var confirm = new ConfirmSpy(result: false); // would block a restart, but this is a fresh run
        vm.ConfirmHandler = confirm.Handle;

        await vm.RunScriptCommand.ExecuteAsync(item);

        Assert.Empty(confirm.Requests);
        Assert.Equal("/x/new.command", Assert.Single(runner.StartCalls));
        Assert.Equal("/x/new.command", vm.SelectedDockEntry?.Path); // Run surfaces its Recent entry
    }

    [Fact]
    public async Task DismissEntry_SelectsNeighbourAtRemovedPosition()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new AppState
        {
            RecentlyRun =
            [
                new RecentRun { Path = "/x/a.command", RanAt = now },
                new RecentRun { Path = "/x/b.command", RanAt = now.AddMinutes(-1) },
                new RecentRun { Path = "/x/c.command", RanAt = now.AddMinutes(-2) },
            ],
        };
        var (vm, _) = BuildVm(state: state);

        // Populate the Recent list (RunScript triggers the rebuild); 'a' is already newest.
        await vm.RunScriptCommand.ExecuteAsync(new ScriptItem("/x/a.command") { DisplayName = "a" });
        Assert.Equal(["/x/a.command", "/x/b.command", "/x/c.command"], vm.Recent.Select(e => e.Path));

        // Dismiss the middle (finished) entry → its position resolves to the next neighbour, 'c'.
        await vm.DismissEntryCommand.ExecuteAsync(vm.Recent[1]);

        Assert.Equal(["/x/a.command", "/x/c.command"], vm.Recent.Select(e => e.Path));
        Assert.Equal("/x/c.command", vm.SelectedDockEntry?.Path);
    }

    [Fact]
    public async Task ShouldConfirmQuit_OnlyWhenKillOnCloseAndSomethingRunning()
    {
        // Kill-on-close on + a running script → confirm.
        var killing = new AppConfig { KillProcessesOnClose = true };
        var (vmKill, _) = BuildVm(config: killing);
        await vmKill.RunScriptCommand.ExecuteAsync(new ScriptItem("/x/run.command") { DisplayName = "run" });
        Assert.True(vmKill.ShouldConfirmQuit());

        // Kill-on-close off + a running script → no confirm (children survive the quit).
        var leaving = new AppConfig { KillProcessesOnClose = false };
        var (vmLeave, _) = BuildVm(config: leaving);
        await vmLeave.RunScriptCommand.ExecuteAsync(new ScriptItem("/x/run.command") { DisplayName = "run" });
        Assert.False(vmLeave.ShouldConfirmQuit());

        // Kill-on-close on but nothing running → nothing to lose.
        var (vmIdle, _) = BuildVm(config: new AppConfig { KillProcessesOnClose = true });
        Assert.False(vmIdle.ShouldConfirmQuit());
    }
}
