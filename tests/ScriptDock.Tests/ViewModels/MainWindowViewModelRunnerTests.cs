using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.Tests.Fakes;
using ScriptDock.Tests.I18n;
using ScriptDock.Tests.Services;
using ScriptDock.ViewModels;
using Xunit;

namespace ScriptDock.Tests.ViewModels;

/// <summary>
/// A launch that never started, driven through the real <see cref="ProcessRunner"/>: a script whose
/// folder is missing cannot be given its working directory, so the runner fails before any process
/// exists. Everything lives in a disposable temporary folder.
/// </summary>
public sealed class MainWindowViewModelRunnerTests : IAsyncLifetime
{
    private readonly ProcessFixture _fixture = new();
    private readonly System.Collections.Generic.List<MainWindowViewModel> _vms = [];
    private string _dir => _fixture.Root;
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public async ValueTask DisposeAsync()
    {
        foreach (var vm in _vms) vm.BeginShutdown();
        try { await _fixture.DisposeAsync(); }
        finally { await Task.WhenAll(_vms.Select(vm => vm.ShutdownAsync())); }
    }

    private (MainWindowViewModel vm, ProcessRunner runner, FakeRecordStore records) BuildVm()
    {
        var config = new AppConfig();
        var state = new AppState();
        var records = new FakeRecordStore();
        var runner = _fixture.Runner();
        var vm = new MainWindowViewModel(
            new FakeConfigStore { Value = config }, new FakeJsonStore<AppState> { Value = state },
            new FakeJsonStore<KnownPaths>(), records, config, state, new KnownPaths(), new ScriptScanner(), runner)
        {
            ConfirmHandler = _ => Task.FromResult(true),
        };
        _vms.Add(vm);
        return (vm, runner, records);
    }

    [Fact]
    public async Task RunScript_WhoseWorkingDirectoryIsMissing_StartsNothing_AndShowsInTheErrorBar()
    {
        var (vm, runner, records) = BuildVm();
        var path = Path.Combine(_dir, "missing", "run.command");

        await vm.RunScriptCommand.ExecuteAsync(new ScriptItem(path) { DisplayName = "run" });

        Assert.Empty(runner.Active);
        Assert.Empty(vm.Recent);
        Assert.Empty(records.Runs);
        Assert.Empty(records.RunEnds);
        Assert.Equal(English.Of("failure.scriptStart"), vm.OperationalError);
    }

    [MacOnlyFact]
    public async Task Restart_WhoseWorkingDirectoryIsGone_KeepsTheRecentRowsLastRun_AndShowsTheErrorThere()
    {
        var (vm, runner, records) = BuildVm();
        var work = Path.Combine(_dir, "work");
        Directory.CreateDirectory(work);
        var path = Path.Combine(work, "sleeper.command");
        var ready = Path.Combine(_dir, "ready");
        // The script says it is running before the folder moves; moved earlier, the shell finds no script.
        File.WriteAllText(path, $"#!/usr/bin/env bash\ntouch '{ready}'\nsleep 60\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var item = new ScriptItem(path) { DisplayName = "sleeper" };

        await vm.RunScriptCommand.ExecuteAsync(item);
        var first = Assert.Single(runner.Active);
        var lastRan = Assert.Single(vm.Recent).LastRanAt;

        await Wait.ForFileAsync(ready, () => File.Exists(ready));

        // The running script keeps its folder under the new name; the restart has no folder to run in.
        Directory.Move(work, Path.Combine(_dir, "moved"));
        await vm.RunScriptCommand.ExecuteAsync(item);

        Assert.Equal(RunState.Terminated, first.State);
        Assert.Empty(runner.Active);
        var entry = Assert.Single(vm.Recent);
        Assert.Equal(lastRan, entry.LastRanAt);
        Assert.Same(entry, vm.SelectedRecentEntry);
        Assert.Single(records.Runs);
        Assert.All(records.RunEnds, end => Assert.Equal(first.Id, end.Run));
        Assert.Equal(English.Of("failure.scriptStart"), vm.RecentActionError);
        Assert.False(vm.HasOperationalError);
    }
}
