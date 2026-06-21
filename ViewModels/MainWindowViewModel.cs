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
/// catalog (tiles) and the Recent list (<see cref="RecentEntry"/> — running and recently-run
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
    private readonly IProcessRunner _runner;

    // The most recent scan's outcome, kept so a hidden/show toggle preserves the new/removed
    // flags until the next scan replaces them.
    private IReadOnlyList<string> _lastFound = [];
    private ISet<string> _newPaths = new HashSet<string>(StringComparer.Ordinal);
    private IReadOnlyList<string> _removed = [];

    private readonly List<ScriptProcess> _subscribed = [];
    private DispatcherTimer? _outputTimer;

    public ObservableCollection<ScriptItem> Scripts { get; } = [];
    public ObservableCollection<RecentEntry> Recent { get; } = [];

    [ObservableProperty] private bool _showHidden;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _status = "Ready.";
    [ObservableProperty] private RecentEntry? _selectedRecentEntry;
    [ObservableProperty] private string _selectedOutput = string.Empty;
    [ObservableProperty] private int _runningCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleHiddenLabel))]
    private ScriptItem? _selectedScript;

    public MainWindowViewModel(
        IJsonStore<AppConfig> configStore,
        IJsonStore<AppState> stateStore,
        AppConfig config,
        AppState state,
        ScriptScanner scanner,
        IProcessRunner runner)
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

    /// <summary>Whether the console input field can send: the selected run is running and accepts input
    /// (a recaptured run does not). Re-evaluated whenever the selected entry changes.</summary>
    public bool CanSendInput => SelectedRecentEntry?.Process is { State: RunState.Running, AcceptsInput: true };

    /// <summary>Whether a Recent entry is selected — drives the Output header's script-name pill.</summary>
    public bool HasSelection => SelectedRecentEntry is not null;

    /// <summary>The Scripts pane's Hide/Show toggle label, reflecting the selected script's current
    /// state. Single-selection list, so the one button serves both directions (Hide a visible script,
    /// Show a hidden one). Defaults to "Hide" when nothing is selected.</summary>
    public string ToggleHiddenLabel => SelectedScript?.IsHidden == true ? "Show" : "Hide";

    /// <summary>Raised after running an input-accepting script, asking the view to focus the console
    /// input field so the user can type immediately. Focus is a view concern, so it is signalled here
    /// rather than performed.</summary>
    public event EventHandler? ConsoleInputFocusRequested;

    /// <summary>Set by the view to confirm a destructive action (the view owns the dialog). Returns
    /// true to proceed. Null when no view is attached (e.g. tests), in which case the action proceeds
    /// unconfirmed.</summary>
    public Func<ConfirmRequest, Task<bool>>? ConfirmHandler { get; set; }

    /// <summary>Whether quitting now would kill running work and so warrants a confirm: only when
    /// Kill-on-close is enabled and something is still running (otherwise the children survive the
    /// quit, so there is nothing to lose). The view drives the actual quit confirmation.</summary>
    public bool ShouldConfirmQuit() => _config.KillProcessesOnClose && RunningCount > 0;

    /// <summary>Starts the console poll, builds the Recent list, and runs the first scan.</summary>
    public async Task InitializeAsync()
    {
        if (_config.RecaptureProcessesOnLaunch)
            _runner.Recapture(_state.RunningProcesses);

        StartOutputTimer();
        RebuildRecent();
        await RescanAsync();
    }

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
        _config.KillProcessesOnClose = draft.KillProcessesOnClose;
        _config.RecaptureProcessesOnLaunch = draft.RecaptureProcessesOnLaunch;
        _configStore.Save(_config);
        Status = "Configuration changed — Rescan to apply.";
    });

    /// <summary>Sends a line to the selected running script's stdin (from the console input field).</summary>
    public void SendInput(string text) => Guard("send input", () =>
    {
        SelectedRecentEntry?.Process?.SendInput(text);
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
    private async Task RunScript(ScriptItem? item) => await GuardAsync("run", async () =>
    {
        if (item is not null)
            await RunByPath(item.Path, item.DisplayName);
    });

    [RelayCommand]
    private async Task RunOrRestart(RecentEntry? entry) => await GuardAsync("run", async () =>
    {
        if (entry is not null)
            await RunByPath(entry.Path, entry.DisplayName);
    });

    [RelayCommand]
    private async Task StopEntry(RecentEntry? entry) => await GuardAsync("stop", async () =>
    {
        if (entry?.Process is not { State: RunState.Running })
            return;

        // Stopping kills the live run, so confirm first.
        if (!await ConfirmAsync("Stop Script", $"“{entry.DisplayName}” is running. Stop it?", "Stop"))
            return;

        _runner.Terminate(entry.Process);
    });

    [RelayCommand]
    private async Task DismissEntry(RecentEntry? entry) => await GuardAsync("dismiss", async () =>
    {
        if (entry is null)
            return;

        // Only dismissing a *running* entry destroys work, so confirm just that case; dismissing a
        // finished entry only drops it from the list (it can be re-run), so it stays immediate.
        if (entry.Process is { State: RunState.Running } &&
            !await ConfirmAsync("Dismiss Script", $"“{entry.DisplayName}” is running. Stop and dismiss it?", "Dismiss"))
            return;

        // Remember the dismissed row's position so focus lands on its neighbour, not nowhere.
        var index = Recent.IndexOf(entry);

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
        Log.Info("ui: dismiss", new { script = entry.Path });
        RebuildRecent();

        // The dismissed path is gone, so RebuildRecent cleared the selection; move it to the
        // neighbour at that position instead (the next entry, or the previous if it was last).
        SelectedRecentEntry = index < 0 || Recent.Count == 0 ? null : Recent[Math.Min(index, Recent.Count - 1)];
    });

    [RelayCommand]
    private void ToggleHidden(ScriptItem? item) => Guard("toggle hidden", () =>
    {
        if (item is null)
            return;

        var nowHidden = !_config.Hidden.Remove(item.Path);
        if (nowHidden)
            _config.Hidden.Add(item.Path);

        _configStore.Save(_config);
        Log.Info("ui: toggle hidden", new { script = item.Path, hidden = nowHidden });
        // Keep the toggled script selected; if hiding made it vanish (Show hidden off), fall to its
        // neighbour so the Scripts selection — and the Hide/Show label — never just resets.
        RebuildScripts(selectNeighbourIfGone: true);
    });

    partial void OnShowHiddenChanged(bool value) => Guard("show hidden", () =>
    {
        _state.ShowHidden = value;
        _stateStore.Save(_state);
        RebuildScripts();
    });

    partial void OnSelectedRecentEntryChanged(RecentEntry? value)
    {
        OnPropertyChanged(nameof(CanSendInput));
        OnPropertyChanged(nameof(HasSelection));
        RefreshOutput();
    }

    private async Task RunByPath(string path, string displayName)
    {
        var running = _runner.Active.FirstOrDefault(p =>
            string.Equals(p.ScriptPath, path, StringComparison.Ordinal) && p.State == RunState.Running);

        if (running is not null)
        {
            // Running an already-running script restarts it — that kills the live run, so confirm.
            if (!await ConfirmAsync("Restart Script", $"“{displayName}” is already running. Restart it?", "Restart"))
                return;
            _runner.Restart(running);
        }
        else
        {
            _runner.Start(path);
        }

        _state.RecentlyRun = RecentRuns.Add(_state.RecentlyRun, path, DateTimeOffset.UtcNow);
        _stateStore.Save(_state);
        RebuildRecent();

        // Surface the just-run script: select its Recent entry so its output shows in the console
        // immediately (selection re-pins the console to the bottom).
        SelectedRecentEntry = Recent.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.Ordinal));

        // A freshly started/restarted run owns a stdin pipe, so move keyboard focus to the console
        // input for immediate typing. Gated on CanSendInput so a non-input run never steals focus.
        if (CanSendInput)
            ConsoleInputFocusRequested?.Invoke(this, EventArgs.Empty);
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

        var selectedPath = SelectedRecentEntry?.Path;

        Recent.Clear();
        foreach (var entry in RecentListBuilder.Build(_state.RecentlyRun, _runner.Active, BuildLabels()))
            Recent.Add(entry);

        SelectedRecentEntry = Recent.FirstOrDefault(e => string.Equals(e.Path, selectedPath, StringComparison.Ordinal));
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

    private void RebuildScripts(bool selectNeighbourIfGone = false)
    {
        var hidden = new HashSet<string>(_config.Hidden, StringComparer.Ordinal);
        var running = new HashSet<string>(
            _runner.Active.Where(p => p.State == RunState.Running).Select(p => p.ScriptPath),
            StringComparer.Ordinal);

        var items = ScriptListBuilder.BuildScripts(_lastFound, _removed, hidden, _newPaths, running, BuildLabels(), ShowHidden);

        // Capture the selection before the rebuild discards the old item instances, so the user's
        // place survives a rebuild (a new scan, a hide/show toggle, or a running-dot refresh).
        var selectedPath = SelectedScript?.Path;
        var selectedIndex = SelectedScript is null ? -1 : Scripts.IndexOf(SelectedScript);

        Scripts.Clear();
        foreach (var item in items)
            Scripts.Add(item);

        // Re-select the same script by path. If it is gone (e.g. just hidden while "Show hidden" is
        // off), optionally drop to the nearest surviving neighbour at that position; otherwise clear.
        // Setting SelectedScript drives both the ListBox selection and the Hide/Show button label.
        var restored = selectedPath is null
            ? null
            : Scripts.FirstOrDefault(s => string.Equals(s.Path, selectedPath, StringComparison.Ordinal));
        if (restored is null && selectNeighbourIfGone && selectedIndex >= 0 && Scripts.Count > 0)
            restored = Scripts[Math.Min(selectedIndex, Scripts.Count - 1)];
        SelectedScript = restored;

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
            var lines = SelectedRecentEntry?.Process?.ReadOutput();
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

    // Async sibling of Guard for the commands that await a confirmation before acting.
    private async Task GuardAsync(string action, Func<Task> body)
    {
        try
        {
            await body();
        }
        catch (Exception ex)
        {
            Log.Error($"ui: {action} failed", ex);
            Status = $"{action} failed — see logs.";
        }
    }

    // Ask the view to confirm a destructive action. With no handler attached (tests) there is no UI
    // to ask, so the action proceeds.
    private async Task<bool> ConfirmAsync(string title, string message, string confirmLabel) =>
        ConfirmHandler is null || await ConfirmHandler(new ConfirmRequest(title, message, confirmLabel));
}
