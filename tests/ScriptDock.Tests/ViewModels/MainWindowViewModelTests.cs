using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ScriptDock.I18n;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.Tests.Fakes;
using ScriptDock.ViewModels;
using ScriptDock.Tests.I18n;
using Xunit;

namespace ScriptDock.Tests.ViewModels;

/// <summary>
/// Orchestration tests for the destructive-action confirm gating and the after-event selection,
/// driven through a <see cref="FakeProcessRunner"/> so no real processes are launched.
/// </summary>
public sealed class MainWindowViewModelTests
{
    private static (MainWindowViewModel vm, FakeProcessRunner runner) BuildVm(
        AppConfig? config = null, AppState? state = null, FakeRecordStore? records = null)
    {
        config ??= new AppConfig();
        state ??= new AppState();
        var configStore = new FakeConfigStore { Value = config };
        var stateStore = new FakeJsonStore<AppState> { Value = state };
        var runner = new FakeProcessRunner();
        var vm = new MainWindowViewModel(
            configStore, stateStore, new FakeJsonStore<KnownPaths>(), records ?? new FakeRecordStore(),
            config, state, new KnownPaths(), new ScriptScanner(), runner);
        return (vm, runner);
    }

    // Records a past run of each path, newest first, the way an earlier session left them.
    private static FakeRecordStore RecordsWithRuns(params string[] paths)
    {
        var records = new FakeRecordStore();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < paths.Length; i++)
            records.Runs.Add(new RunRecord("2025-12-31T00:00:00.000Z", i + 1, now.AddMinutes(-i), paths[i], null, null, null));
        return records;
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
        var entry = new RecentEntry("/x/run.command", "run.command", DateTimeOffset.UtcNow, process);
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
        var entry = new RecentEntry("/x/run.command", "run.command", DateTimeOffset.UtcNow, process);
        vm.ConfirmHandler = new ConfirmSpy(result: true).Handle;

        await vm.StopEntryCommand.ExecuteAsync(entry);

        Assert.Same(process, Assert.Single(runner.TerminateCalls));
    }

    [Fact]
    public async Task DismissEntry_FinishedEntry_DoesNotConfirm_AndRemoves()
    {
        var records = RecordsWithRuns("/x/done.command");
        var (vm, _) = BuildVm(records: records);
        await vm.LoadRecentAsync();
        var entry = new RecentEntry("/x/done.command", "done.command", DateTimeOffset.UtcNow, process: null);
        var confirm = new ConfirmSpy(result: false); // would block if consulted
        vm.ConfirmHandler = confirm.Handle;

        await vm.DismissEntryCommand.ExecuteAsync(entry);

        Assert.Empty(confirm.Requests); // a finished entry is reversible — no prompt
        Assert.Equal(["/x/done.command"], records.Dismissals);
        Assert.Empty(vm.Recent);
        Assert.Empty(await records.ReadRecentAsync());
    }

    [Fact]
    public async Task DismissEntry_RunningEntry_DeclinedConfirm_KeepsItAndDoesNotTerminate()
    {
        var records = RecordsWithRuns("/x/live.command");
        var (vm, runner) = BuildVm(records: records);
        var process = runner.AddRunning("/x/live.command");
        var entry = new RecentEntry("/x/live.command", "live.command", DateTimeOffset.UtcNow, process);
        vm.ConfirmHandler = new ConfirmSpy(result: false).Handle;

        await vm.DismissEntryCommand.ExecuteAsync(entry);

        Assert.Empty(runner.TerminateCalls);
        Assert.Empty(runner.DismissCalls);
        Assert.Empty(records.Dismissals);
    }

    [Fact]
    public async Task DismissEntry_RunningEntry_AcceptedConfirm_TerminatesAndRemoves()
    {
        var records = RecordsWithRuns("/x/live.command");
        var (vm, runner) = BuildVm(records: records);
        await vm.LoadRecentAsync();
        var process = runner.AddRunning("/x/live.command");
        var entry = new RecentEntry("/x/live.command", "live.command", DateTimeOffset.UtcNow, process);
        vm.ConfirmHandler = new ConfirmSpy(result: true).Handle;

        await vm.DismissEntryCommand.ExecuteAsync(entry);

        Assert.Same(process, Assert.Single(runner.TerminateCalls));
        Assert.Same(process, Assert.Single(runner.DismissCalls));
        Assert.Equal(["/x/live.command"], records.Dismissals);
        Assert.Empty(vm.Recent);
    }

    [Fact]
    public async Task DismissEntry_WhenTerminationIsUnconfirmed_RetainsOwnershipAndRecentEntry()
    {
        var records = RecordsWithRuns("/x/live.command");
        var (vm, runner) = BuildVm(records: records);
        runner.TerminateResult = false;
        var process = runner.AddRunning("/x/live.command");
        vm.ConfirmHandler = new ConfirmSpy(result: true).Handle;

        await vm.DismissEntryCommand.ExecuteAsync(
            new RecentEntry("/x/live.command", "live.command", DateTimeOffset.UtcNow, process));

        Assert.Same(process, Assert.Single(runner.TerminateCalls));
        Assert.Empty(runner.DismissCalls);
        Assert.Empty(records.Dismissals);
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
    public async Task RunOrRestart_SecondCallWhileFirstInFlight_IsGatedByCanExecute_NeverStartsASecondProcess()
    {
        // SD-1: MainWindow's double-tap/keydown handlers must check CanExecute before Execute, exactly
        // like this test does, so a restart-in-progress (still "Running" during the termination grace)
        // can't be re-triggered into a second, concurrent RestartAsync that would start a second
        // invisible child process. Direct ICommand.Execute ignores CanExecute entirely, which is the
        // bug: this test's second call would land as a real second restart without the guard.
        var (vm, runner) = BuildVm();
        runner.AddRunning("/x/dev.command");
        var item = new RecentEntry("/x/dev.command", "dev.command", DateTimeOffset.UtcNow, runner.Active[0]);
        vm.ConfirmHandler = new ConfirmSpy(result: true).Handle;
        runner.RestartGate = new TaskCompletionSource();

        var command = vm.RunOrRestartCommand;
        Assert.True(command.CanExecute(item));
        var first = command.ExecuteAsync(item);

        // The restart is now in flight (blocked on RestartGate): CanExecute must already report false,
        // the same signal the fixed code-behind checks before ever calling Execute again.
        Assert.False(command.CanExecute(item));
        if (command.CanExecute(item))
            command.Execute(item); // what the fixed handlers do; must not fire while gated

        runner.RestartGate.SetResult();
        await first;

        Assert.Single(runner.RestartCalls); // never a second, concurrent restart of the same handle
    }

    [Fact]
    public async Task RunScript_WhenOldTreeDoesNotExit_DoesNotRecordOrLaunchReplacement()
    {
        var records = new FakeRecordStore();
        var (vm, runner) = BuildVm(records: records);
        runner.RestartResult = false;
        runner.AddRunning("/x/dev.command");
        vm.ConfirmHandler = new ConfirmSpy(result: true).Handle;

        await vm.RunScriptCommand.ExecuteAsync(new ScriptItem("/x/dev.command") { DisplayName = "dev" });

        Assert.Empty(runner.StartCalls);
        Assert.Empty(records.Runs);
        Assert.Contains("no replacement", vm.RecentActionError, StringComparison.OrdinalIgnoreCase);
        Assert.True(vm.HasRecentActionError);
        Assert.False(vm.HasOperationalError);
    }

    [Fact]
    public async Task ProcessActionFailures_StayWithTheirPath_AndSuccessfulRetryClearsOnlyItsOperation()
    {
        var (vm, runner) = BuildVm();
        runner.TerminateResult = false;
        var process = runner.AddRunning("/x/live.command");
        var entry = new RecentEntry("/x/live.command", "live.command", DateTimeOffset.UtcNow, process);
        var other = new RecentEntry("/x/other.command", "other.command", DateTimeOffset.UtcNow, process: null);
        vm.ConfirmHandler = new ConfirmSpy(result: true).Handle;
        vm.SelectedRecentEntry = entry;

        await vm.StopEntryCommand.ExecuteAsync(entry);
        await vm.SendInputAsync("hello");

        Assert.Equal(2, vm.RecentActionErrorCount);
        Assert.True(vm.HasMultipleRecentActionErrors);
        Assert.False(vm.HasOperationalError);

        vm.SelectedRecentEntry = other;
        Assert.False(vm.HasRecentActionError);
        vm.SelectedRecentEntry = entry;
        Assert.True(vm.HasRecentActionError);

        vm.DismissRecentActionErrorCommand.Execute(null);
        Assert.Equal(1, vm.RecentActionErrorCount);
        Assert.Contains("send input", vm.RecentActionError, StringComparison.OrdinalIgnoreCase);

        runner.TerminateResult = true;
        await vm.StopEntryCommand.ExecuteAsync(entry);
        Assert.False(vm.HasRecentActionError);
    }

    [Fact]
    public async Task StoppingAProcess_ClearsObsoleteRuntimeErrors_ButKeepsItsIndependentHistoryFailure()
    {
        var config = new AppConfig();
        var state = new AppState();
        var runner = new FakeProcessRunner();
        var vm = new MainWindowViewModel(
            new FakeConfigStore { Value = config },
            new FakeJsonStore<AppState> { Value = state },
            new FakeJsonStore<KnownPaths>(),
            new FakeRecordStore { ThrowOnWrite = true },
            config,
            state,
            new KnownPaths(),
            new ScriptScanner(),
            runner);
        vm.ConfirmHandler = new ConfirmSpy(result: true).Handle;

        await vm.RunScriptCommand.ExecuteAsync(new ScriptItem("/x/live.command") { DisplayName = "live.command" });
        await vm.SendInputAsync("hello");
        Assert.Equal(2, vm.RecentActionErrorCount);

        await vm.StopEntryCommand.ExecuteAsync(vm.SelectedRecentEntry);

        Assert.Equal(1, vm.RecentActionErrorCount);
        Assert.Equal(English.Of("process.historyFailed"), vm.RecentActionError);
        Assert.False(vm.HasOperationalError);
    }

    [Fact]
    public void ShellActionFailures_KeepIndependentKeysInTheGlobalQueue()
    {
        var (vm, _) = BuildVm();

        vm.ReportShellActionError("open-about", ScriptDock.I18n.Message.Of("shell.aboutFailed"));
        vm.ReportShellActionError("open-records", ScriptDock.I18n.Message.Of("shell.recordsFailed"));
        vm.ResolveShellActionError("open-about");

        Assert.True(vm.HasOperationalError);
        Assert.Equal(1, vm.OperationalErrorCount);
        Assert.Equal(English.Of("shell.recordsFailed"), vm.OperationalError);
    }

    [Fact]
    public async Task CaptureWindowPlacement_IsPersistedWithPaneSizes()
    {
        var configStore = new FakeConfigStore();
        var stateStore = new FakeJsonStore<AppState>();
        var vm = new MainWindowViewModel(
            configStore, stateStore, new FakeJsonStore<KnownPaths>(), new FakeRecordStore(), configStore.Value, stateStore.Value,
            new KnownPaths(), new ScriptScanner(), new FakeProcessRunner());

        vm.CaptureWindowPlacement(-1400, 80, 1100.5, 720.25, maximized: true);
        await vm.PersistPaneSizesAsync(420, 240);

        Assert.Equal(1, stateStore.SaveCount);
        Assert.Equal(-1400, vm.WindowPositionX);
        Assert.Equal(80, vm.WindowPositionY);
        Assert.Equal(1100.5, vm.WindowWidth);
        Assert.Equal(720.25, vm.WindowHeight);
        Assert.True(vm.WindowMaximized);
    }

    [Fact]
    public async Task TryApplySettings_SaveFailureDoesNotPublishCandidate()
    {
        var config = new AppConfig { RootDirs = ["/old"], UiFontFamily = "Inter" };
        var configStore = new FakeConfigStore { Value = config, ThrowOnSave = true };
        var stateStore = new FakeJsonStore<AppState>();
        var vm = new MainWindowViewModel(
            configStore, stateStore, new FakeJsonStore<KnownPaths>(), new FakeRecordStore(), config, new AppState(), new KnownPaths(), new ScriptScanner(), new FakeProcessRunner());
        var draft = vm.CreateSettingsDraft();
        draft.RootDirs.Clear();
        draft.RootDirs.Add("/new");
        draft.UiFontFamily = "Iosevka";

        Assert.False(await vm.TryApplySettingsAsync(draft));
        Assert.Equal(["/old"], config.RootDirs);
        Assert.Equal("Inter", config.UiFontFamily);
        Assert.False(vm.HasOperationalError);
        Assert.Equal(0, vm.OperationalErrorCount);
    }

    [Fact]
    public async Task SettingsSaveFailure_RemainsOwnedByDialogAndDoesNotPublishGlobalError()
    {
        var config = new AppConfig { RootDirs = [] };
        var configStore = new FakeConfigStore { Value = config, ThrowOnSave = true };
        var stateStore = new FakeJsonStore<AppState>();
        var vm = new MainWindowViewModel(
            configStore, stateStore, new FakeJsonStore<KnownPaths>(), new FakeRecordStore(), config, stateStore.Value, new KnownPaths(), new ScriptScanner(), new FakeProcessRunner());
        var draft = vm.CreateSettingsDraft();
        draft.UiFontFamily = "Helvetica";

        Assert.False(await vm.TryApplySettingsAsync(draft));
        Assert.False(await vm.TryApplySettingsAsync(draft));
        Assert.Equal(0, vm.OperationalErrorCount);

        await vm.RescanCommand.ExecuteAsync(null); // ordinary successful activity stays with the Scripts pane
        Assert.False(vm.HasOperationalError);
        Assert.Equal(0, vm.OperationalErrorCount);
        Assert.True(vm.HasCatalogResult);
    }

    [Fact]
    public async Task TryApplySettings_SurfacesTheRescanConsequenceAtTheCatalogOwner()
    {
        var (vm, _) = BuildVm();
        var draft = vm.CreateSettingsDraft();
        draft.UiFontFamily = "Helvetica";

        Assert.True(await vm.TryApplySettingsAsync(draft));

        Assert.Contains("Rescan", vm.CatalogResult, StringComparison.Ordinal);
        Assert.False(vm.HasOperationalError);
    }

    [Fact]
    public async Task TryApplySettings_SavedFontChangeRaisesLiveRemeasureSignal()
    {
        var config = new AppConfig { UiFontFamily = "" };
        var vm = new MainWindowViewModel(
            new FakeConfigStore { Value = config },
            new FakeJsonStore<AppState>(),
            new FakeJsonStore<KnownPaths>(),
            new FakeRecordStore(),
            config,
            new AppState(),
            new KnownPaths(),
            new ScriptScanner(),
            new FakeProcessRunner());
        var fontChanges = 0;
        vm.UiFontChanged += (_, _) => fontChanges++;
        var draft = vm.CreateSettingsDraft();
        draft.UiFontFamily = "Helvetica";

        Assert.True(await vm.TryApplySettingsAsync(draft));
        Assert.Equal(1, fontChanges);
    }

    [Fact]
    public async Task RunEnd_IsRecordedForTheRunThisSessionStarted()
    {
        var records = new FakeRecordStore();
        var (vm, runner) = BuildVm(records: records);
        await vm.RunScriptCommand.ExecuteAsync(new ScriptItem("/x/end.command") { DisplayName = "end.command" });
        var started = Assert.Single(runner.Active);

        started.Complete();

        var end = Assert.Single(records.RunEnds);
        Assert.Equal((records.Session, started.Id), (end.RunSession, end.Run));
        Assert.Equal("exited", end.State);
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
        Assert.Equal("/x/new.command", vm.SelectedRecentEntry?.Path); // Run surfaces its Recent entry
    }

    [Fact]
    public async Task RunScript_FreshRun_EnablesInput_AndRequestsConsoleFocus()
    {
        var (vm, _) = BuildVm();
        var focusRequested = false;
        vm.ConsoleInputFocusRequested += (_, _) => focusRequested = true;

        await vm.RunScriptCommand.ExecuteAsync(new ScriptItem("/x/dev.command") { DisplayName = "dev" });

        // The runner started the run and owns its stdin pipe, so the selected entry can take input
        // and the VM asked the view to focus the console.
        Assert.True(vm.CanSendInput);
        Assert.True(focusRequested);
    }

    [Fact]
    public async Task DismissEntry_SelectsNeighbourAtRemovedPosition()
    {
        var (vm, _) = BuildVm(records: RecordsWithRuns("/x/a.command", "/x/b.command", "/x/c.command"));
        await vm.LoadRecentAsync();

        // Populate the Recent list (RunScript triggers the rebuild); 'a' is already newest.
        await vm.RunScriptCommand.ExecuteAsync(new ScriptItem("/x/a.command") { DisplayName = "a" });
        Assert.Equal(["/x/a.command", "/x/b.command", "/x/c.command"], vm.Recent.Select(e => e.Path));

        // Dismiss the middle (finished) entry → its position resolves to the next neighbour, 'c'.
        await vm.DismissEntryCommand.ExecuteAsync(vm.Recent[1]);

        Assert.Equal(["/x/a.command", "/x/c.command"], vm.Recent.Select(e => e.Path));
        Assert.Equal("/x/c.command", vm.SelectedRecentEntry?.Path);
    }

    [Fact]
    public async Task ShouldConfirmQuit_WhenSomethingRuns()
    {
        // Quitting stops every running script, so a running one warrants a confirm.
        var (running, _) = BuildVm();
        await running.RunScriptCommand.ExecuteAsync(new ScriptItem("/x/run.command") { DisplayName = "run" });
        Assert.True(running.ShouldConfirmQuit());

        // Nothing running → nothing to lose.
        var (idle, _) = BuildVm();
        Assert.False(idle.ShouldConfirmQuit());
    }

    [Fact]
    public async Task ShutdownAsync_StopsEveryRunningScript()
    {
        var (vm, runner) = BuildVm();
        await vm.RunScriptCommand.ExecuteAsync(new ScriptItem("/x/run.command") { DisplayName = "run" });

        await vm.ShutdownAsync();

        Assert.Equal(1, runner.StopAllCalls);
        Assert.False(vm.HasOperationalError);
    }

    [Fact]
    public async Task RunScript_ThatNeverStarted_StaysOutOfRecent_AndShowsInTheErrorBar()
    {
        var records = new FakeRecordStore();
        var (vm, runner) = BuildVm(records: records);
        runner.StartException = new InvalidOperationException("boom");
        var item = new ScriptItem("/x/new.command") { DisplayName = "new" };

        await vm.RunScriptCommand.ExecuteAsync(item);

        Assert.Empty(vm.Recent);
        Assert.Empty(records.Runs);
        Assert.Null(vm.SelectedRecentEntry);
        Assert.Equal(English.Of("failure.scriptStart"), vm.OperationalError);

        // A later launch that does start enters Recent and clears that error.
        runner.StartException = null;
        await vm.RunScriptCommand.ExecuteAsync(item);

        Assert.Equal(["/x/new.command"], vm.Recent.Select(e => e.Path));
        Assert.False(vm.HasOperationalError);
    }

    [Fact]
    public async Task RunScript_ThatNeverStarted_KeepsItsRecentRowsLastRun_AndShowsTheErrorThere()
    {
        var records = RecordsWithRuns("/x/old.command");
        var (vm, runner) = BuildVm(records: records);
        await vm.InitializeAsync();
        var lastRan = Assert.Single(vm.Recent).LastRanAt;
        runner.StartException = new InvalidOperationException("boom");

        await vm.RunScriptCommand.ExecuteAsync(new ScriptItem("/x/old.command") { DisplayName = "old" });

        var entry = Assert.Single(vm.Recent);
        Assert.Equal(lastRan, entry.LastRanAt);
        Assert.Single(records.Runs);
        Assert.Same(entry, vm.SelectedRecentEntry);
        Assert.Equal(English.Of("failure.scriptStart"), vm.RecentActionError);
        Assert.False(vm.HasOperationalError);
    }

    [Fact]
    public async Task InitializeAsync_ShowsEachRecentRowsRecordedEnd()
    {
        // An earlier session's run that exited 0, as a launcher whose shell hands its app to the system does.
        var records = RecordsWithRuns("/x/rebuild.command");
        records.RunEnds.Add(new RunEnd("2025-12-31T00:00:00.000Z", 1, DateTimeOffset.UtcNow, "exited", 0));
        var (vm, _) = BuildVm(records: records);

        await vm.InitializeAsync();

        var entry = Assert.Single(vm.Recent);
        Assert.Null(entry.Process);
        Assert.Equal(Localizer.T("recent.stateExited"), entry.StatePillText);
    }
}
