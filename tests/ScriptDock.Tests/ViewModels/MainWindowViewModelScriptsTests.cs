using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ScriptDock;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.Storage;
using ScriptDock.Tests.Fakes;
using ScriptDock.Tests.Storage;
using ScriptDock.ViewModels;
using Xunit;

namespace ScriptDock.Tests.ViewModels;

/// <summary>
/// Scripts-pane selection behaviour through <c>RebuildScripts</c>, exercised the way the app drives it:
/// a real <see cref="ScriptScanner"/> over a temp directory populates the list, then a rescan or a
/// hide/show toggle rebuilds it. Selection must survive a rebuild by path, and fall to the
/// position-neighbour when the selected script vanishes (hidden while "Show hidden" is off).
/// Joins the SCRIPTDOCK_DATA_DIR collection so anything the view model resolves under the storage root
/// lands in a temp directory and the suite never touches the real <c>~/.scriptdock</c>.
/// </summary>
[Collection(StorageRootEnvironment.CollectionName)]
public sealed class MainWindowViewModelScriptsTests : IDisposable
{
    private readonly string _root;          // scanned for scripts
    private readonly string _home;          // SCRIPTDOCK_DATA_DIR
    private readonly string? _previousHome;

    public MainWindowViewModelScriptsTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "scriptdock-scripts-tests", NanoId.New());
        _root = Path.Combine(baseDir, "scripts");
        _home = Path.Combine(baseDir, "home");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_home);

        _previousHome = Environment.GetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable);
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, _previousHome);
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private void Touch(string name) => File.WriteAllText(Path.Combine(_root, name), "#");

    private static string Name(ScriptItem item) => Path.GetFileName(item.Path);

    private async Task<MainWindowViewModel> ScannedVm(AppState? state = null)
    {
        var config = new AppConfig { RootDirs = [_root], Extensions = [".command"] };
        state ??= new AppState();
        var vm = new MainWindowViewModel(
            new FakeConfigStore { Value = config },
            new FakeJsonStore<AppState> { Value = state },
            new FakeJsonStore<KnownPaths>(),
            new FakeRecordStore(),
            config, state, new KnownPaths(), new ScriptScanner(), new FakeProcessRunner());
        await vm.RescanCommand.ExecuteAsync(null);
        return vm;
    }

    [Fact]
    public async Task InitializeAsync_RecapturedRunningProcess_AppearsOnceInRecent()
    {
        var path = Path.Combine(_root, "live.command");
        // A previous session's run has no recorded end, so it may still be running; it is also in the recent list.
        var records = new FakeRecordStore();
        records.Runs.Add(new RunRecord("2025-12-31T00:00:00.000Z", 1, DateTimeOffset.UtcNow, path, 4242, DateTimeOffset.UtcNow, null));
        var state = new AppState();
        var config = new AppConfig { RootDirs = [_root], Extensions = [".command"], RecaptureProcessesOnLaunch = true };
        var vm = new MainWindowViewModel(
            new FakeConfigStore { Value = config },
            new FakeJsonStore<AppState> { Value = state },
            new FakeJsonStore<KnownPaths>(),
            records,
            config, state, new KnownPaths(), new ScriptScanner(), new FakeProcessRunner());

        await vm.InitializeAsync(); // recapture → read Recent → RebuildRecent → scan

        // The recaptured run and its recent entry share a path → exactly one row, shown running.
        var entry = Assert.Single(vm.Recent, e => e.Path == path);
        Assert.True(entry.IsRunning);
        Assert.Equal(1, vm.RunningCount);
    }

    [Fact]
    public async Task InitializeAsync_RecapturesOnlyUnendedRuns_AndRecordsTheGoneOnesEnd()
    {
        var live = Path.Combine(_root, "live.command");
        var ended = Path.Combine(_root, "ended.command");
        var gone = Path.Combine(_root, "gone.command");
        var records = new FakeRecordStore();
        records.Runs.Add(new RunRecord("2025-12-31T00:00:00.000Z", 1, DateTimeOffset.UtcNow, live, 11, DateTimeOffset.UtcNow, null));
        records.Runs.Add(new RunRecord("2025-12-31T00:00:00.000Z", 2, DateTimeOffset.UtcNow, ended, 12, DateTimeOffset.UtcNow, null));
        records.Runs.Add(new RunRecord("2025-12-31T00:00:00.000Z", 3, DateTimeOffset.UtcNow, gone, 13, DateTimeOffset.UtcNow, null));
        records.RunEnds.Add(new RunEnd("2025-12-31T00:00:00.000Z", 2, DateTimeOffset.UtcNow, "exited", 0));
        var runner = new FakeProcessRunner();
        runner.GoneScripts.Add(gone);
        var state = new AppState();
        var config = new AppConfig { RootDirs = [_root], Extensions = [".command"], RecaptureProcessesOnLaunch = true };
        var vm = new MainWindowViewModel(
            new FakeConfigStore { Value = config },
            new FakeJsonStore<AppState> { Value = state },
            new FakeJsonStore<KnownPaths>(),
            records,
            config, state, new KnownPaths(), new ScriptScanner(), runner);

        await vm.InitializeAsync();

        Assert.Equal([live, gone], runner.RecaptureCalls.Select(run => run.ScriptPath));
        var goneEnd = Assert.Single(records.RunEnds, end => end.Run == 3);
        Assert.Equal(RunEnd.Gone, goneEnd.State);
        Assert.Null(goneEnd.ExitCode);

        // The recaptured run's end names the run it was recorded as, not this session's handle.
        var recaptured = Assert.Single(runner.Active);
        recaptured.Complete();
        var liveEnd = Assert.Single(records.RunEnds, end => end.Run == 1);
        Assert.Equal("2025-12-31T00:00:00.000Z", liveEnd.RunSession);
        Assert.Equal("exited", liveEnd.State);
    }

    [Fact]
    public async Task InitializeAsync_RecaptureFailureIsRetainedWithoutRawDiagnosticsAndScanContinues()
    {
        var hostile = "EACCES IPC /private/tmp/SCRIPTDOCK-RECAPTURE-SENTINEL";
        var runner = new FakeProcessRunner { RecaptureException = new IOException(hostile) };
        var config = new AppConfig
        {
            RootDirs = [_root],
            Extensions = [".command"],
            RecaptureProcessesOnLaunch = true,
        };
        Touch("still-scanned.command");
        var state = new AppState();
        var vm = new MainWindowViewModel(
            new FakeConfigStore { Value = config },
            new FakeJsonStore<AppState> { Value = state },
            new FakeJsonStore<KnownPaths>(),
            new FakeRecordStore(),
            config, state, new KnownPaths(), new ScriptScanner(), runner);

        await vm.InitializeAsync();

        Assert.True(vm.HasOperationalError);
        Assert.Contains("could not be restored", vm.OperationalError, StringComparison.Ordinal);
        Assert.DoesNotContain(hostile, vm.OperationalError, StringComparison.Ordinal);
        Assert.Single(vm.Scripts);
    }

    [Fact]
    public async Task RebuildScripts_PreservesSelectionByPath_AcrossRescan()
    {
        Touch("a.command");
        Touch("b.command");
        Touch("c.command");
        var vm = await ScannedVm();
        Assert.Equal(3, vm.Scripts.Count);

        var b = vm.Scripts.Single(s => Name(s) == "b.command");
        vm.SelectedScript = b;

        await vm.RescanCommand.ExecuteAsync(null); // rebuild from a fresh scan

        Assert.NotNull(vm.SelectedScript);
        Assert.Equal("b.command", Name(vm.SelectedScript!)); // re-selected by path
        Assert.NotSame(b, vm.SelectedScript);                // ...onto the fresh instance
    }

    [Fact]
    public async Task ToggleHidden_OnSelected_WithShowHiddenOff_SelectsPositionNeighbour()
    {
        Touch("a.command");
        Touch("b.command");
        Touch("c.command");
        var vm = await ScannedVm();

        vm.SelectedScript = vm.Scripts.Single(s => Name(s) == "b.command"); // the middle item

        await vm.ToggleHiddenCommand.ExecuteAsync(vm.SelectedScript); // hide it; "Show hidden" is off → it leaves the list

        Assert.DoesNotContain(vm.Scripts, s => Name(s) == "b.command");
        Assert.Equal(["a.command", "c.command"], vm.Scripts.Select(Name));
        // b sat at index 1; the neighbour now at that position is c — selection lands there, not nowhere.
        Assert.Equal("c.command", Name(vm.SelectedScript!));
    }

    [Fact]
    public async Task ToggleHidden_OnLastSelected_WithShowHiddenOff_FallsBackToPreviousNeighbour()
    {
        Touch("a.command");
        Touch("b.command");
        var vm = await ScannedVm();

        vm.SelectedScript = vm.Scripts.Single(s => Name(s) == "b.command"); // the last item

        await vm.ToggleHiddenCommand.ExecuteAsync(vm.SelectedScript);

        // Hiding the last item clamps the neighbour index back to the new last — 'a'.
        Assert.Equal("a.command", Name(vm.SelectedScript!));
    }

    [Fact]
    public async Task ToggleHidden_OnSelected_WithShowHiddenOn_KeepsSelection_AndFlipsLabel()
    {
        Touch("a.command");
        Touch("b.command");
        Touch("c.command");
        var vm = await ScannedVm();
        vm.ShowHidden = true; // rebuilds; nothing hidden yet, so the list is unchanged

        var b = vm.Scripts.Single(s => Name(s) == "b.command");
        vm.SelectedScript = b;
        Assert.Equal("Hide", vm.ToggleHiddenLabel);

        await vm.ToggleHiddenCommand.ExecuteAsync(b); // hide b, but "Show hidden" is on → it stays visible

        var selected = vm.SelectedScript!;
        Assert.Equal("b.command", Name(selected)); // selection preserved on the same path
        Assert.True(selected.IsHidden);
        Assert.Equal("Show", vm.ToggleHiddenLabel); // the one button now offers to Show it
    }
}
