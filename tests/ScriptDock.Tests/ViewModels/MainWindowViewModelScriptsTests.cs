using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ScriptDock;
using ScriptDock.I18n;
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
    public async Task InitializeAsync_WithUnendedRunsInTheRecords_StartsNoProcessAndShowsNoError()
    {
        var path = Path.Combine(_root, "left.command");
        Touch("left.command");
        // A previous session's run with a process id and no recorded end: ScriptDock no longer owns it.
        var records = new FakeRecordStore();
        records.Runs.Add(new RunRecord("2025-12-31T00:00:00.000Z", 1, DateTimeOffset.UtcNow, path, 4242, DateTimeOffset.UtcNow, null));
        var runner = new FakeProcessRunner();
        var state = new AppState();
        var config = new AppConfig { RootDirs = [_root], Extensions = [".command"] };
        var vm = new MainWindowViewModel(
            new FakeConfigStore { Value = config },
            new FakeJsonStore<AppState> { Value = state },
            new FakeJsonStore<KnownPaths>(),
            records,
            config, state, new KnownPaths(), new ScriptScanner(), runner);

        await vm.InitializeAsync();

        Assert.Empty(runner.StartCalls);
        Assert.Empty(runner.Active);
        Assert.False(vm.HasOperationalError);
        Assert.Empty(records.RunEnds);
        var entry = Assert.Single(vm.Recent);
        Assert.Equal(path, entry.Path);
        Assert.False(entry.IsRunning);
        Assert.Equal(Localizer.T("recent.stateUnknown"), entry.StatePillText);
        Assert.Equal(0, vm.RunningCount);
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

    [Fact]
    public async Task RunScript_ClearsItsNewFlag_AndLeavesTheOthersNew()
    {
        Touch("a.command");
        Touch("b.command");
        var config = new AppConfig { RootDirs = [_root], Extensions = [".command"] };
        var state = new AppState();
        var vm = new MainWindowViewModel(
            new FakeConfigStore { Value = config },
            new FakeJsonStore<AppState> { Value = state },
            new FakeJsonStore<KnownPaths>(),
            new FakeRecordStore(),
            config, state, new KnownPaths { Paths = [] }, new ScriptScanner(), new FakeProcessRunner());
        await vm.RescanCommand.ExecuteAsync(null);
        Assert.All(vm.Scripts, s => Assert.True(s.IsNew));

        await vm.RunScriptCommand.ExecuteAsync(vm.Scripts.Single(s => Name(s) == "a.command"));

        Assert.False(vm.Scripts.Single(s => Name(s) == "a.command").IsNew);
        Assert.True(vm.Scripts.Single(s => Name(s) == "b.command").IsNew);

        // Show hidden rebuilds the list from the same scan and changes neither flag.
        vm.ShowHidden = true;
        Assert.False(vm.Scripts.Single(s => Name(s) == "a.command").IsNew);
        Assert.True(vm.Scripts.Single(s => Name(s) == "b.command").IsNew);
        vm.ShowHidden = false;
        Assert.False(vm.Scripts.Single(s => Name(s) == "a.command").IsNew);
        Assert.True(vm.Scripts.Single(s => Name(s) == "b.command").IsNew);
    }

    // The app's own known-paths store over the temp home, loaded the way the composition root loads it.
    private async Task<MainWindowViewModel> ScannedWithRealKnownPaths()
    {
        var store = new JsonStore<KnownPaths>(AppPaths.KnownPathsFileName, "known paths", recordBackups: false, rebuildable: true);
        var config = new AppConfig { RootDirs = [_root], Extensions = [".command"] };
        var state = new AppState();
        var vm = new MainWindowViewModel(
            new FakeConfigStore { Value = config },
            new FakeJsonStore<AppState> { Value = state },
            store,
            new FakeRecordStore(),
            config, state, store.Load(), new ScriptScanner(), new FakeProcessRunner());
        await vm.RescanCommand.ExecuteAsync(null);
        return vm;
    }

    private string KnownPathsFile => Path.Combine(_home, AppPaths.KnownPathsFileName);

    [Fact]
    public async Task Rescan_WithNoKnownPathsFile_FlagsNothing_ThenComparesAgainstWhatItSaved()
    {
        Touch("a.command");

        var first = await ScannedWithRealKnownPaths();
        Assert.False(Assert.Single(first.Scripts).IsNew);

        // The first scan saved what it found, so the next launch compares against it.
        Touch("b.command");
        var next = await ScannedWithRealKnownPaths();
        Assert.False(next.Scripts.Single(s => Name(s) == "a.command").IsNew);
        Assert.True(next.Scripts.Single(s => Name(s) == "b.command").IsNew);
    }

    [Fact]
    public async Task Rescan_ThatFindsTheSamePaths_DoesNotRewriteKnownPaths()
    {
        Touch("a.command");
        var vm = await ScannedWithRealKnownPaths();
        var earlier = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(KnownPathsFile, earlier);

        await vm.RescanCommand.ExecuteAsync(null);
        Assert.Equal(earlier, File.GetLastWriteTimeUtc(KnownPathsFile));

        // A scan that finds something else does write.
        Touch("b.command");
        await vm.RescanCommand.ExecuteAsync(null);
        Assert.NotEqual(earlier, File.GetLastWriteTimeUtc(KnownPathsFile));
    }

    [Fact]
    public async Task Rescan_WithAnUnreadableKnownPathsFile_FlagsNothingNewOrRemoved()
    {
        Touch("a.command");
        File.WriteAllText(KnownPathsFile, "{ not json");

        var vm = await ScannedWithRealKnownPaths();

        var item = Assert.Single(vm.Scripts);
        Assert.Equal(ScriptFlag.None, item.Flag);
    }

    [Fact]
    public async Task Rescan_WithASavedEmptyList_FlagsEveryScriptNew()
    {
        Touch("a.command");
        Touch("b.command");
        File.WriteAllText(KnownPathsFile, """{"paths":[]}""");

        var vm = await ScannedWithRealKnownPaths();

        Assert.All(vm.Scripts, s => Assert.True(s.IsNew));
    }

    private (MainWindowViewModel Vm, FakeRecordStore Records, FakeTimeProvider Clock) ActivatableVm(KnownPaths? known = null)
    {
        var config = new AppConfig { RootDirs = [_root], Extensions = [".command"] };
        var state = new AppState();
        var records = new FakeRecordStore();
        var clock = new FakeTimeProvider();
        var vm = new MainWindowViewModel(
            new FakeConfigStore { Value = config },
            new FakeJsonStore<AppState> { Value = state },
            new FakeJsonStore<KnownPaths>(),
            records,
            config, state, known ?? new KnownPaths(), new ScriptScanner(), new FakeProcessRunner()) { Time = clock };
        return (vm, records, clock);
    }

    // Lets the activation wait pass, then the scan it started finish.
    private static async Task SettleActivation(MainWindowViewModel vm, FakeTimeProvider clock)
    {
        clock.Advance(MainWindowViewModel.ActivationRescanDelay);
        Dispatcher.UIThread.RunJobs();
        await vm.ActivationScan;
    }

    private ScriptFlag FlagOf(MainWindowViewModel vm, string name) => vm.Scripts.Single(s => Name(s) == name).Flag;

    // The keys of every line the Scripts pane's result shows, in order, from now on.
    private static List<string?> WatchCatalogResult(MainWindowViewModel vm)
    {
        var shown = new List<string?>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.CatalogResultMessage))
                shown.Add(vm.CatalogResultMessage?.Key);
        };
        return shown;
    }

    [AvaloniaFact]
    public async Task Activation_KeepsNewFlags_FlagsWhatChanged_AndSpeaksOnlyWhenSomethingDid()
    {
        Touch("a.command");
        Touch("b.command");
        var (vm, _, clock) = ActivatableVm(new KnownPaths { Paths = [] });
        await vm.InitializeAsync();
        Assert.Equal(ScriptFlag.New, FlagOf(vm, "a.command"));
        var shown = WatchCatalogResult(vm);

        // Nothing changed: the flags stay and the pane says nothing, not even that it is scanning.
        vm.OnWindowActivated();
        await SettleActivation(vm, clock);
        Assert.Empty(shown);
        Assert.Equal(ScriptFlag.New, FlagOf(vm, "a.command"));
        Assert.Equal(ScriptFlag.New, FlagOf(vm, "b.command"));

        // One added, one removed: the earlier flag stays, the change is flagged, and only its result shows.
        Touch("c.command");
        File.Delete(Path.Combine(_root, "b.command"));
        vm.OnWindowActivated();
        await SettleActivation(vm, clock);
        Assert.Equal(["scan.addedAndRemoved"], shown);
        Assert.Equal(ScriptFlag.New, FlagOf(vm, "a.command"));
        Assert.Equal(ScriptFlag.Removed, FlagOf(vm, "b.command"));
        Assert.Equal(ScriptFlag.New, FlagOf(vm, "c.command"));

        // Rescan still says it is scanning, then how it ended, and ends every flag.
        shown.Clear();
        await vm.RescanCommand.ExecuteAsync(null);
        Assert.Equal(["scan.running", "scan.upToDate"], shown);
        Assert.Equal(["a.command", "c.command"], vm.Scripts.Select(Name));
        Assert.All(vm.Scripts, s => Assert.Equal(ScriptFlag.None, s.Flag));
        await vm.ShutdownAsync();
    }

    [AvaloniaFact]
    public async Task Activation_AfterSettingsChange_ClearsTheRescanToApplyLine_ItAnswered()
    {
        Touch("a.command");
        var (vm, _, clock) = ActivatableVm(new KnownPaths { Paths = [] });
        await vm.InitializeAsync();

        var scan = vm.CreateSettingsDraft();
        scan.IgnorePatterns.Add("*.tmp");
        Assert.True(await vm.TryApplySettingsAsync(scan));
        Assert.Equal("scan.configChanged", vm.CatalogResultMessage?.Key);

        // The quiet rescan applies the new settings and finds nothing to report, so the line asking for a
        // Rescan goes rather than staying on screen after it has been answered.
        vm.OnWindowActivated();
        await SettleActivation(vm, clock);
        Assert.Null(vm.CatalogResultMessage);
        await vm.ShutdownAsync();
    }

    [AvaloniaFact]
    public async Task Rescan_DuringAnActivationScan_StaysAvailable()
    {
        Touch("a.command");
        var (vm, _, clock) = ActivatableVm();
        await vm.InitializeAsync();

        vm.OnWindowActivated();
        clock.Advance(MainWindowViewModel.ActivationRescanDelay);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.RescanCommand.CanExecute(null));
        await vm.ActivationScan;
        await vm.ShutdownAsync();
    }

    [Fact]
    public async Task TryApplySettings_ThatChangesTheScan_EndsTheNewFlags_AndOneThatDoesNot_KeepsThem()
    {
        Touch("a.command");
        var config = new AppConfig { RootDirs = [_root], Extensions = [".command"] };
        var state = new AppState();
        var vm = new MainWindowViewModel(
            new FakeConfigStore { Value = config },
            new FakeJsonStore<AppState> { Value = state },
            new FakeJsonStore<KnownPaths>(),
            new FakeRecordStore(),
            config, state, new KnownPaths { Paths = [] }, new ScriptScanner(), new FakeProcessRunner());
        await vm.RescanCommand.ExecuteAsync(null);

        var font = vm.CreateSettingsDraft();
        font.UiFontFamily = "Helvetica";
        Assert.True(await vm.TryApplySettingsAsync(font));
        Assert.Equal(ScriptFlag.New, FlagOf(vm, "a.command"));

        var scan = vm.CreateSettingsDraft();
        scan.IgnorePatterns.Add("*.tmp");
        Assert.True(await vm.TryApplySettingsAsync(scan));
        Assert.Equal(ScriptFlag.None, FlagOf(vm, "a.command"));
    }

    [AvaloniaFact]
    public async Task Activation_RescansOnce_ForABurstOfActivations_AndKeepsTheSelection()
    {
        Touch("a.command");
        Touch("b.command");
        var (vm, records, clock) = ActivatableVm();
        await vm.InitializeAsync();
        vm.SelectedScript = vm.Scripts.Single(s => Name(s) == "b.command");
        var scans = records.ScanReports.Count;
        Touch("c.command");

        vm.OnWindowActivated();
        clock.Advance(MainWindowViewModel.ActivationRescanDelay / 2);
        vm.OnWindowActivated();
        vm.OnWindowActivated();
        await SettleActivation(vm, clock);

        Assert.Equal(scans + 1, records.ScanReports.Count);
        Assert.Contains(vm.Scripts, s => Name(s) == "c.command");
        Assert.Equal("b.command", Name(vm.SelectedScript!));
        await vm.ShutdownAsync(); // stops the timers InitializeAsync started
    }

    [AvaloniaFact]
    public async Task Activation_DoesNotRescan_WhileADialogIsOpen_OrAScanRuns_OrBeforeTheFirstScan()
    {
        var (vm, records, clock) = ActivatableVm();

        // Before the first scan has run, an activation is the window opening, not coming back.
        vm.OnWindowActivated();
        clock.Advance(MainWindowViewModel.ActivationRescanDelay);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(records.ScanReports);

        await vm.InitializeAsync();
        var scans = records.ScanReports.Count;

        var dialogOpen = true;
        vm.IsDialogOpen = () => dialogOpen;
        vm.OnWindowActivated();
        await SettleActivation(vm, clock);
        Assert.Equal(scans, records.ScanReports.Count);

        dialogOpen = false;
        vm.IsScanning = true;
        vm.OnWindowActivated();
        await SettleActivation(vm, clock);
        Assert.Equal(scans, records.ScanReports.Count);
        vm.IsScanning = false;

        // Once the window shuts down, a late activation starts nothing.
        await vm.ShutdownAsync();
        vm.OnWindowActivated();
        clock.Advance(MainWindowViewModel.ActivationRescanDelay);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(scans, records.ScanReports.Count);
    }
}
