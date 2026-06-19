using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.Storage;

namespace ScriptDock.ViewModels;

/// <summary>
/// Root view model. Drives scanning (<see cref="ScriptScanner"/>) and launching
/// (<see cref="ProcessRunner"/>), and exposes the two lists the window binds to: the Scripts
/// catalog (tiles) and the Recent list (<see cref="DockEntry"/> — running and recently-run
/// scripts merged, kept until dismissed). Every command and callback is guarded so a single
/// failure logs and degrades rather than crashing the window — ScriptDock owns the user's
/// running scripts, so it must not go down.
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly IJsonStore<AppConfig> _configStore;
    private readonly IJsonStore<AppState> _stateStore;
    private readonly AppConfig _config;
    private readonly AppState _state;
    private readonly ScriptScanner _scanner;
    private readonly ProcessRunner _runner;

    // The most recent scan's outcome, kept so a hidden/show toggle preserves the new/removed
    // flags until the next scan replaces them.
    private IReadOnlyList<string> _lastFound = [];
    private ISet<string> _newPaths = new HashSet<string>(StringComparer.Ordinal);
    private IReadOnlyList<string> _removed = [];

    private readonly List<ScriptProcess> _subscribed = [];
    private DispatcherTimer? _outputTimer;

    public ObservableCollection<ScriptItem> Scripts { get; } = [];
    public ObservableCollection<DockEntry> Recent { get; } = [];

    [ObservableProperty] private bool _showHidden;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _status = "Ready.";
    [ObservableProperty] private DockEntry? _selectedDockEntry;
    [ObservableProperty] private string _selectedOutput = string.Empty;
    [ObservableProperty] private int _runningCount;

    public MainWindowViewModel(
        IJsonStore<AppConfig> configStore,
        IJsonStore<AppState> stateStore,
        AppConfig config,
        AppState state,
        ScriptScanner scanner,
        ProcessRunner runner)
    {
        _configStore = configStore;
        _stateStore = stateStore;
        _config = config;
        _state = state;
        _scanner = scanner;
        _runner = runner;
        _showHidden = state.ShowHidden; // field, not property: no save/rebuild during construction

        _runner.ProcessesChanged += (_, _) => Dispatcher.UIThread.Post(RebuildFromProcesses);
    }

    public double? SavedRecentWidth => _state.RecentPaneWidth;
    public double? SavedConsoleHeight => _state.ConsoleHeight;

    /// <summary>Drive the lists' empty-state messages. Refreshed after each rebuild.</summary>
    public bool NoScripts => Scripts.Count == 0;
    public bool NoRecent => Recent.Count == 0;

    /// <summary>Starts the console poll, builds the Recent list, and runs the first scan.</summary>
    public async Task InitializeAsync()
    {
        if (_config.RecaptureProcessesOnLaunch)
            _runner.Recapture(_state.RunningProcesses);

        StartOutputTimer();
        RebuildRecent();
        await RescanAsync();
        SeedTestNewFlags();
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // TEMPORARY TEST SCAFFOLD — MUST BE REMOVED BEFORE RELEASE.
    // Randomly marks ~1/5 of the scanned scripts as "just detected" (New) on launch so the
    // amber just-scanned styling is always visible during human testing. Tracked in the
    // refinement plan ("Remove the just-scanned test scaffold before release"). The fake flags
    // clear on the next real Rescan, which recomputes _newPaths from the actual scan diff.
    private void SeedTestNewFlags()
    {
        if (_lastFound.Count == 0)
            return;

        foreach (var path in _lastFound)
            if (Random.Shared.Next(5) == 0)
                _newPaths.Add(path);

        RebuildScripts();
    }
    // ─────────────────────────────────────────────────────────────────────────────

    public void PersistPaneSizes(double recentWidth, double consoleHeight) => Guard("save pane sizes", () =>
    {
        _state.RecentPaneWidth = recentWidth;
        _state.ConsoleHeight = consoleHeight;
        _stateStore.Save(_state);
    });

    public void Shutdown() => Guard("shutdown", () =>
    {
        _outputTimer?.Stop();
        _runner.ShutdownAll(_config.KillProcessesOnClose);
        PersistRunningSnapshot(); // record what is still running (or none, if killed) for next launch
    });

    // Record the live running set so a relaunch can recapture it (see ProcessRunner.Recapture).
    // Called whenever the running set changes and on shutdown.
    private void PersistRunningSnapshot()
    {
        _state.RunningProcesses = _runner.Active
            .Where(p => p.State == RunState.Running)
            .Select(ToPersisted)
            .OfType<PersistedProcess>()
            .ToList();
        _stateStore.Save(_state);
    }

    private static PersistedProcess? ToPersisted(ScriptProcess process)
    {
        if (process.Pid is not { } pid || process.OsStartedAt is not { } osStartedAt)
            return null;

        return new PersistedProcess
        {
            Pid = pid,
            OsStartedAt = osStartedAt,
            LaunchedAt = process.StartedAt,
            ScriptPath = process.ScriptPath,
            LogFilePath = process.LogFilePath ?? string.Empty,
        };
    }

    public SettingsDialogViewModel CreateSettingsDraft() => new(_config);

    public void ApplySettings(SettingsDialogViewModel draft) => Guard("apply settings", () =>
    {
        _config.RootDirs = draft.RootDirs.ToList();
        _config.Extensions = draft.Extensions.ToList();
        _config.IgnorePatterns = draft.IgnorePatterns.ToList();
        _configStore.Save(_config);
        Status = "Configuration changed — Rescan to apply.";
    });

    [RelayCommand]
    private async Task RescanAsync()
    {
        if (IsScanning)
            return;

        IsScanning = true;
        Status = "Scanning…";
        try
        {
            var roots = _config.RootDirs.ToList();
            var extensions = _config.Extensions.ToList();
            var patterns = _config.IgnorePatterns.ToList();

            var report = await Task.Run(() => _scanner.Scan(roots, extensions, patterns));
            ScanReportLog.Write(report);

            var diff = ScanDiff.Compute(report.Found, _state.KnownPaths);
            _lastFound = report.Found;
            _newPaths = new HashSet<string>(diff.Added, StringComparer.Ordinal);
            _removed = diff.Removed;

            RebuildScripts();

            _state.KnownPaths = report.Found.ToList();
            _stateStore.Save(_state);

            Status = $"{report.Found.Count} script(s) — {diff.Added.Count} new, {diff.Removed.Count} removed.";
        }
        catch (Exception ex)
        {
            Log.Error("ui: rescan failed", ex);
            Status = "Scan failed — see logs.";
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private void RunScript(ScriptItem? item) => Guard("run", () =>
    {
        if (item is not null)
            RunByPath(item.Path);
    });

    [RelayCommand]
    private void RunOrRestart(DockEntry? entry) => Guard("run", () =>
    {
        if (entry is not null)
            RunByPath(entry.Path);
    });

    [RelayCommand]
    private void StopEntry(DockEntry? entry) => Guard("stop", () =>
    {
        if (entry?.Process is { State: RunState.Running })
            _runner.Terminate(entry.Process);
    });

    [RelayCommand]
    private void DismissEntry(DockEntry? entry) => Guard("dismiss", () =>
    {
        if (entry is null)
            return;

        if (entry.Process is not null)
        {
            if (entry.Process.State == RunState.Running)
                _runner.Terminate(entry.Process);
            _runner.Dismiss(entry.Process);
        }

        _state.RecentlyRun = _state.RecentlyRun
            .Where(r => !string.Equals(r.Path, entry.Path, StringComparison.Ordinal))
            .ToList();
        _stateStore.Save(_state);
        RebuildRecent();
    });

    [RelayCommand]
    private void ToggleHidden(ScriptItem? item) => Guard("toggle hidden", () =>
    {
        if (item is null)
            return;

        if (!_config.Hidden.Remove(item.Path))
            _config.Hidden.Add(item.Path);

        _configStore.Save(_config);
        RebuildScripts();
    });

    partial void OnShowHiddenChanged(bool value) => Guard("show hidden", () =>
    {
        _state.ShowHidden = value;
        _stateStore.Save(_state);
        RebuildScripts();
    });

    partial void OnSelectedDockEntryChanged(DockEntry? value) => RefreshOutput();

    private void RunByPath(string path)
    {
        var running = _runner.Active.FirstOrDefault(p =>
            string.Equals(p.ScriptPath, path, StringComparison.Ordinal) && p.State == RunState.Running);

        if (running is not null)
            _runner.Restart(running);
        else
            _runner.Start(path);

        _state.RecentlyRun = RecentRuns.Add(_state.RecentlyRun, path, DateTimeOffset.UtcNow);
        _stateStore.Save(_state);
        RebuildRecent();

        // Surface the just-run script: select its Recent entry so its output shows in the console
        // immediately (selection re-pins the console to the bottom).
        SelectedDockEntry = Recent.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.Ordinal));
    }

    private void RebuildFromProcesses() => Guard("refresh", () =>
    {
        RebuildRecent();
        RebuildScripts(); // refresh the tiles' running dots
        PersistRunningSnapshot();
    });

    private void OnProcessStateChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(RebuildFromProcesses);

    private void RebuildRecent()
    {
        // Re-subscribe StateChanged across the current active set so a running→stopped
        // transition refreshes the list; unsubscribe the rest to avoid a leak.
        foreach (var process in _subscribed)
            process.StateChanged -= OnProcessStateChanged;
        _subscribed.Clear();
        foreach (var process in _runner.Active)
        {
            process.StateChanged += OnProcessStateChanged;
            _subscribed.Add(process);
        }

        var selectedPath = SelectedDockEntry?.Path;

        Recent.Clear();
        foreach (var entry in DockListBuilder.Build(_state.RecentlyRun, _runner.Active, BuildLabels()))
            Recent.Add(entry);

        SelectedDockEntry = Recent.FirstOrDefault(e => string.Equals(e.Path, selectedPath, StringComparison.Ordinal));
        RunningCount = _runner.Active.Count(p => p.State == RunState.Running);
        OnPropertyChanged(nameof(NoRecent));
        RefreshOutput();
    }

    // The shortest unambiguous label per path, over every path any list could show, so a
    // script reads identically in the Scripts tiles and the Recent list.
    private IReadOnlyDictionary<string, string> BuildLabels()
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in _lastFound) paths.Add(path);
        foreach (var path in _removed) paths.Add(path);
        foreach (var run in _state.RecentlyRun) paths.Add(run.Path);
        foreach (var process in _runner.Active) paths.Add(process.ScriptPath);

        // ScriptLabels joins its minimal-unique segments with '/', but that '/' is not a real
        // relative path — it is a breadcrumb between dedup segments — so present it as one.
        return ScriptLabels.Build(paths)
            .ToDictionary(kv => kv.Key, kv => kv.Value.Replace("/", " › "), StringComparer.Ordinal);
    }

    private void RebuildScripts()
    {
        var hidden = new HashSet<string>(_config.Hidden, StringComparer.Ordinal);
        var running = new HashSet<string>(
            _runner.Active.Where(p => p.State == RunState.Running).Select(p => p.ScriptPath),
            StringComparer.Ordinal);

        var items = ScriptListBuilder.BuildScripts(_lastFound, _removed, hidden, _newPaths, running, BuildLabels(), ShowHidden);

        Scripts.Clear();
        foreach (var item in items)
            Scripts.Add(item);
        OnPropertyChanged(nameof(NoScripts));
    }

    private void StartOutputTimer()
    {
        if (_outputTimer is not null)
            return;

        _outputTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _outputTimer.Tick += (_, _) => OnOutputTick();
        _outputTimer.Start();
    }

    private void OnOutputTick()
    {
        _runner.ReconcileExited(); // backstop for a missed Exited event
        RefreshOutput();
    }

    private void RefreshOutput()
    {
        try
        {
            var lines = SelectedDockEntry?.Process?.ReadOutput();
            SelectedOutput = lines is null ? string.Empty : string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex)
        {
            Log.Warn("ui: refresh output failed", ex);
        }
    }

    private void Guard(string action, Action body)
    {
        try
        {
            body();
        }
        catch (Exception ex)
        {
            Log.Error($"ui: {action} failed", ex);
            Status = $"{action} failed — see logs.";
        }
    }
}
