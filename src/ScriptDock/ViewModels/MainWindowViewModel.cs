using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
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

    // Cancels the in-flight scan when a newer scan supersedes it or the window closes, so a scan over
    // a slow/unresponsive root can never strand IsScanning (and thus the Rescan command) forever.
    private CancellationTokenSource? _scanCts;

    public ObservableCollection<ScriptItem> Scripts { get; } = [];
    public ObservableCollection<RecentEntry> Recent { get; } = [];

    [ObservableProperty] private bool _showHidden;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCatalogResult))]
    private string _catalogResult = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOperationalError))]
    private string _operationalError = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMultipleOperationalErrors))]
    [NotifyPropertyChangedFor(nameof(OperationalErrorCountText))]
    private int _operationalErrorCount;
    [ObservableProperty] private RecentEntry? _selectedRecentEntry;
    [ObservableProperty] private string _selectedOutput = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecentActionError))]
    private string _recentActionError = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMultipleRecentActionErrors))]
    [NotifyPropertyChangedFor(nameof(RecentActionErrorCountText))]
    private int _recentActionErrorCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRunning))]
    private int _runningCount;

    // Status-bar persistent facts: total scripts found and how many of those are hidden.
    [ObservableProperty] private int _scriptCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHidden))]
    private int _hiddenCount;

    private readonly List<OperationalErrorEntry> _operationalErrors = [];
    private readonly Dictionary<string, List<ProcessActionErrorEntry>> _processActionErrors =
        new(PathIdentity.Comparer);
    private DispatcherTimer? _catalogResultTimer;

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

        ApplyUiFont();
        _runner.ProcessesChanged += (_, _) => Dispatcher.UIThread.Post(RebuildFromProcesses);
    }

    /// <summary>
    /// Applies the configured UI (chrome) font app-wide by overriding the <c>AppFontFamily</c> resource
    /// the Window style binds via DynamicResource, so it takes effect live across every window. The
    /// read-only output console renders in its own monospace font and is unaffected.
    /// </summary>
    private void ApplyUiFont()
    {
        if (Application.Current is { } app)
        {
            app.Resources["AppFontFamily"] = UiFont.Resolve(_config.UiFontFamily);
        }
    }

    public bool HasOperationalError => OperationalError.Length > 0;
    public bool HasMultipleOperationalErrors => OperationalErrorCount > 1;
    public string OperationalErrorCountText => $"{OperationalErrorCount} errors";
    public bool HasCatalogResult => CatalogResult.Length > 0;
    public bool HasRecentActionError => RecentActionError.Length > 0;
    public bool HasMultipleRecentActionErrors => RecentActionErrorCount > 1;
    public string RecentActionErrorCountText => $"{RecentActionErrorCount} errors";

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

    /// <summary>Status-bar fact toggles: the running dot/segment and the hidden segment show only when non-zero.</summary>
    public bool HasRunning => RunningCount > 0;
    public bool HasHidden => HiddenCount > 0;

    /// <summary>Raised after running an input-accepting script, asking the view to focus the console
    /// input field so the user can type immediately. Focus is a view concern, so it is signalled here
    /// rather than performed.</summary>
    public event EventHandler? ConsoleInputFocusRequested;

    /// <summary>Raised after a saved UI-font change updates the dynamic app resource. The window
    /// owns measurement and native minimum sizing, so it remeasures after the new font lays out.</summary>
    public event EventHandler? UiFontChanged;

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
        _catalogResultTimer?.Stop();
        _scanCts?.Cancel(); // don't let a slow scan keep the closing window's work alive
        _runner.ShutdownAll(_config.KillProcessesOnClose);
        PersistRunningSnapshot(); // record what is still running (or none, if killed) for next launch
    });

    // The running snapshot last written to disk, as a cheap signature (pid:path per run), so a
    // process event that doesn't actually change the running set doesn't rewrite state.json. Null
    // until the first persist; "" means "persisted, nothing running".
    private string? _persistedRunningSignature;

    // Record the live running set so a relaunch can recapture it (see ProcessRunner.Recapture).
    // Called whenever the running set may have changed and on shutdown — but only writes when it
    // actually did, since this is driven by every process event (RebuildFromProcesses).
    internal void PersistRunningSnapshot()
    {
        var running = _runner.Active
            .Where(p => p.State == RunState.Running)
            .Select(ToPersisted)
            .OfType<PersistedProcess>()
            .ToList();

        var signature = string.Join("|", running.Select(p =>
            $"{p.Pid}:{p.OsStartedAt.ToUnixTimeMilliseconds()}:{p.LaunchedAt.ToUnixTimeMilliseconds()}:{p.ScriptPath}:{p.LogFilePath}"));
        if (signature == _persistedRunningSignature)
            return;

        _state.RunningProcesses = running;
        _stateStore.Save(_state);
        _persistedRunningSignature = signature;
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

    public bool TryApplySettings(SettingsDialogViewModel draft)
    {
        var candidate = new AppConfig
        {
            RootDirs = draft.RootDirs.ToList(),
            Extensions = draft.Extensions.ToList(),
            IgnorePatterns = draft.IgnorePatterns.ToList(),
            Hidden = _config.Hidden.ToList(),
            KillProcessesOnClose = draft.KillProcessesOnClose,
            RecaptureProcessesOnLaunch = draft.RecaptureProcessesOnLaunch,
            UiFontFamily = draft.UiFontFamily.Trim(),
        };

        try
        {
            _configStore.Save(candidate);
        }
        catch (Exception ex)
        {
            Log.Error("ui: apply settings failed", ex);
            return false;
        }

        var fontChanged = !string.Equals(_config.UiFontFamily, candidate.UiFontFamily, StringComparison.Ordinal);
        _config.RootDirs = candidate.RootDirs;
        _config.Extensions = candidate.Extensions;
        _config.IgnorePatterns = candidate.IgnorePatterns;
        _config.Hidden = candidate.Hidden;
        _config.KillProcessesOnClose = candidate.KillProcessesOnClose;
        _config.RecaptureProcessesOnLaunch = candidate.RecaptureProcessesOnLaunch;
        _config.UiFontFamily = candidate.UiFontFamily;
        ApplyUiFont();
        if (fontChanged)
            UiFontChanged?.Invoke(this, EventArgs.Empty);
        ShowCatalogResult("Configuration changed — Rescan to apply.");
        return true;
    }

    /// <summary>Sends a line to the selected running script's stdin (from the console input field).</summary>
    public async Task<bool> SendInputAsync(string text)
    {
        var entry = SelectedRecentEntry;
        if (entry?.Process is not { } process)
            return false;

        try
        {
            var sent = await process.SendInputAsync(text);
            if (sent)
                ResolveProcessActionError(entry.Path, "send-input");
            else
                ReportProcessActionError(entry.Path, "send-input", "Couldn’t send input. Try again while the script is running.");
            return sent;
        }
        catch (Exception ex)
        {
            Log.Error("ui: send input failed", ex);
            ReportProcessActionError(entry.Path, "send-input", "Couldn’t send input. Try again while the script is running.");
            return false;
        }
    }

    [RelayCommand]
    private async Task RescanAsync()
    {
        // Supersede any in-flight scan rather than refusing to start: a previous scan stuck on a
        // slow/unresponsive root must not disable Rescan forever. The latest scan owns IsScanning.
        _scanCts?.Cancel();
        var cts = new CancellationTokenSource();
        _scanCts = cts;

        IsScanning = true;
        ShowCatalogResult("Scanning…");
        try
        {
            var roots = _config.RootDirs.ToList();
            var extensions = _config.Extensions.ToList();
            var patterns = _config.IgnorePatterns.ToList();

            var report = await Task.Run(() => _scanner.Scan(roots, extensions, patterns, cts.Token), cts.Token);
            cts.Token.ThrowIfCancellationRequested(); // a newer scan superseded us between completion and here
            ScanReportLog.Write(report);

            var diff = ScanDiff.Compute(report.Found, _state.KnownPaths);
            _lastFound = report.Found;
            _newPaths = new HashSet<string>(diff.Added.Select(PathIdentity.Key), PathIdentity.Comparer);
            _removed = diff.Removed;

            RebuildScripts();

            _state.KnownPaths = report.Found.ToList();
            _stateStore.Save(_state);

            ResolveOperationalError("scan");
            ShowCatalogResult(ScanResultMessage(diff), transient: true);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer scan (or cancelled on shutdown); that scan owns the status now.
        }
        catch (Exception ex)
        {
            Log.Error("ui: rescan failed", ex);
            ClearCatalogResult();
            ReportOperationalError("scan", "Scan failed — see logs.");
        }
        finally
        {
            // Only the most recent scan clears IsScanning, so a superseded scan completing late can't
            // flip the flag out from under the one now running.
            if (ReferenceEquals(_scanCts, cts))
            {
                IsScanning = false;
                _scanCts = null;
            }
            cts.Dispose();
        }
    }

    [RelayCommand]
    private async Task RunScript(ScriptItem? item)
    {
        if (item is not null)
            await RunByPath(item.Path, item.DisplayName);
    }

    [RelayCommand]
    private async Task RunOrRestart(RecentEntry? entry)
    {
        if (entry is not null)
            await RunByPath(entry.Path, entry.DisplayName);
    }

    [RelayCommand]
    private async Task StopEntry(RecentEntry? entry)
    {
        if (entry?.Process is not { State: RunState.Running })
            return;

        try
        {
            // Stopping kills the live run, so confirm first.
            if (!await ConfirmAsync("Stop Script", $"“{entry.DisplayName}” is running. Stop it?", "Stop"))
                return;

            if (!await _runner.TerminateAsync(entry.Process))
                ReportProcessActionError(entry.Path, "stop", "The script did not stop; ScriptDock is still tracking it.");
            else
                ResolveStoppedProcessActionErrors(entry.Path);
        }
        catch (Exception ex)
        {
            Log.Error("ui: stop failed", ex, new { script = entry.Path });
            ReportProcessActionError(entry.Path, "stop", "The script did not stop; ScriptDock is still tracking it.");
        }
    }

    [RelayCommand]
    private async Task DismissEntry(RecentEntry? entry)
    {
        if (entry is null)
            return;

        try
        {
            // Only dismissing a *running* entry destroys work, so confirm just that case; dismissing a
            // finished entry only drops it from the list (it can be re-run), so it stays immediate.
            if (entry.Process is { State: RunState.Running } &&
                !await ConfirmAsync("Dismiss Script", $"“{entry.DisplayName}” is running. Stop and dismiss it?", "Dismiss"))
                return;

            // Remember the dismissed row's position so focus lands on its neighbour, not nowhere.
            var index = Recent.IndexOf(entry);

            if (entry.Process is not null)
            {
                if (entry.Process.State == RunState.Running && !await _runner.TerminateAsync(entry.Process))
                {
                    ReportProcessActionError(entry.Path, "dismiss", "The script did not stop; it was not dismissed.");
                    return;
                }
                _runner.Dismiss(entry.Process);
            }

            var previousRecents = _state.RecentlyRun;
            _state.RecentlyRun = previousRecents
                .Where(r => !PathIdentity.Same(r.Path, entry.Path))
                .ToList();
            try
            {
                _stateStore.Save(_state);
            }
            catch
            {
                _state.RecentlyRun = previousRecents;
                throw;
            }

            _processActionErrors.Remove(PathIdentity.Key(entry.Path));
            Log.Info("ui: dismiss", new { script = entry.Path });
            RebuildRecent();

            // The dismissed path is gone, so RebuildRecent cleared the selection; move it to the
            // neighbour at that position instead (the next entry, or the previous if it was last).
            SelectedRecentEntry = index < 0 || Recent.Count == 0 ? null : Recent[Math.Min(index, Recent.Count - 1)];
        }
        catch (Exception ex)
        {
            Log.Error("ui: dismiss failed", ex, new { script = entry.Path });
            ReportProcessActionError(entry.Path, "dismiss", "The script could not be dismissed. Check the log and try again.");
        }
    }

    [RelayCommand]
    private void ToggleHidden(ScriptItem? item) => Guard("toggle hidden", () =>
    {
        if (item is null)
            return;

        var removedHidden = _config.Hidden.RemoveAll(path => PathIdentity.Same(path, item.Path));
        var nowHidden = removedHidden == 0;
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
        RefreshRecentActionErrorProjection();
    }

    private async Task RunByPath(string path, string displayName)
    {
        ScriptProcess? started;
        try
        {
            var running = _runner.Active.FirstOrDefault(p =>
                PathIdentity.Same(p.ScriptPath, path) && p.State == RunState.Running);

            if (running is not null)
            {
                // Running an already-running script restarts it — that kills the live run, so confirm.
                if (!await ConfirmAsync("Restart Script", $"“{displayName}” is already running. Restart it?", "Restart"))
                    return;
                started = await _runner.RestartAsync(running);
                if (started is null)
                {
                    ReportProcessActionError(path, "restart", "The existing script did not stop; no replacement was launched.");
                    RebuildRecent();
                    SelectRecentPath(path);
                    return;
                }
                ResolveProcessActionError(path, "restart");
            }
            else
            {
                started = _runner.Start(path);
            }
        }
        catch (Exception ex)
        {
            Log.Error("ui: run failed", ex, new { script = path });
            EnsureRecentPath(path);
            RebuildRecent();
            SelectRecentPath(path);
            ReportProcessActionError(path, "run", "The script could not be started. Check the log and try again.");
            return;
        }

        _state.RecentlyRun = RecentRuns.Add(_state.RecentlyRun, path, DateTimeOffset.UtcNow);
        try
        {
            _stateStore.Save(_state);
        }
        catch (Exception ex)
        {
            Log.Error("ui: save recent run failed", ex, new { script = path });
            ReportProcessActionError(path, "recent-history", "The script ran, but its recent history could not be saved.");
        }
        RebuildRecent();

        // Surface the just-run script: select its Recent entry so its output shows in the console
        // immediately (selection re-pins the console to the bottom).
        SelectRecentPath(path);

        // The row is stable by path, but a successful Run/Restart produces a new process generation.
        // Failures tied to the prior process no longer have a surviving consequence on this row.
        ResolveReplacedProcessActionErrors(path);

        if (started.State == RunState.Failed)
            ReportProcessActionError(path, "run", "The script could not be started. Check its output and try again.");
        else
            ResolveProcessActionError(path, "run");

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

        SelectedRecentEntry = selectedPath is null ? null : Recent.FirstOrDefault(e => PathIdentity.Same(e.Path, selectedPath));
        RunningCount = _runner.Active.Count(p => p.State == RunState.Running);
        OnPropertyChanged(nameof(NoRecent));
        RefreshOutput();
    }

    // Memo for the label map, keyed on the exact set of paths it was built from. The dedup result
    // depends only on that set, so it is recomputed only when the set changes — not on every refresh,
    // and not twice per refresh (RebuildRecent and RebuildScripts both ask within one rebuild).
    private HashSet<string>? _labelPaths;
    private IReadOnlyDictionary<string, string> _labels = new Dictionary<string, string>(StringComparer.Ordinal);

    // The shortest unambiguous label per path, over every path any list could show, so a
    // script reads identically in the Scripts tiles and the Recent list.
    private IReadOnlyDictionary<string, string> BuildLabels()
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in _lastFound) paths.Add(path);
        foreach (var path in _removed) paths.Add(path);
        foreach (var run in _state.RecentlyRun) paths.Add(run.Path);
        foreach (var process in _runner.Active) paths.Add(process.ScriptPath);

        if (_labelPaths is not null && _labelPaths.SetEquals(paths))
            return _labels;

        // ScriptLabels joins its minimal-unique segments with '/', but that '/' is not a real
        // relative path — it is a breadcrumb between dedup segments — so present it as one.
        _labels = ScriptLabels.Build(paths)
            .ToDictionary(kv => kv.Key, kv => kv.Value.Replace("/", " › "), StringComparer.Ordinal);
        _labelPaths = paths;
        return _labels;
    }

    private void RebuildScripts(bool selectNeighbourIfGone = false)
    {
        var hidden = new HashSet<string>(_config.Hidden.Select(PathIdentity.Key), PathIdentity.Comparer);
        var running = new HashSet<string>(
            _runner.Active.Where(p => p.State == RunState.Running).Select(p => PathIdentity.Key(p.ScriptPath)),
            PathIdentity.Comparer);

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
            : Scripts.FirstOrDefault(s => PathIdentity.Same(s.Path, selectedPath));
        if (restored is null && selectNeighbourIfGone && selectedIndex >= 0 && Scripts.Count > 0)
            restored = Scripts[Math.Min(selectedIndex, Scripts.Count - 1)];
        SelectedScript = restored;

        // Status-bar facts: total found, and how many of those are hidden.
        ScriptCount = _lastFound.Count;
        HiddenCount = _lastFound.Count(path => hidden.Contains(PathIdentity.Key(path)));
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

    // The process and the exact line list last rendered into SelectedOutput. ReadOutput returns the
    // same cached list instance while the run's log hasn't grown, so on a steady tick this lets us
    // skip re-joining a large tail (up to 256 KB) into a string that would only be discarded as equal.
    private ScriptProcess? _renderedOutputProcess;
    private IReadOnlyList<string>? _renderedOutputLines;

    private void RefreshOutput()
    {
        try
        {
            var process = SelectedRecentEntry?.Process;
            var lines = process?.ReadOutput();
            if (SelectedRecentEntry is { } selected)
                ResolveProcessActionError(selected.Path, "read-output");

            // Cache hit: same run, same (reference-identical) tail as last render — nothing to redo.
            if (ReferenceEquals(process, _renderedOutputProcess) && ReferenceEquals(lines, _renderedOutputLines))
                return;

            _renderedOutputProcess = process;
            _renderedOutputLines = lines;
            SelectedOutput = lines is null ? string.Empty : string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex)
        {
            Log.Warn("ui: refresh output failed", ex);
            if (SelectedRecentEntry is { } selected)
                ReportProcessActionError(selected.Path, "read-output", "Couldn’t refresh this script’s output.");
        }
    }

    private void Guard(string action, Action body)
    {
        try
        {
            body();
            ResolveOperationalError(action);
        }
        catch (Exception ex)
        {
            Log.Error($"ui: {action} failed", ex);
            ReportOperationalError(action, $"{action} failed — see logs.");
        }
    }

    // Ask the view to confirm a destructive action. With no handler attached (tests) there is no UI
    // to ask, so the action proceeds.
    private async Task<bool> ConfirmAsync(string title, string message, string confirmLabel) =>
        ConfirmHandler is null || await ConfirmHandler(new ConfirmRequest(title, message, confirmLabel));

    private sealed record OperationalErrorEntry(string Key, string Message);
    private sealed record ProcessActionErrorEntry(string Key, string Message);

    internal void ReportShellActionError(string key, string message) => ReportOperationalError(key, message);

    internal void ResolveShellActionError(string key) => ResolveOperationalError(key);

    private void ReportOperationalError(string key, string message)
    {
        var index = _operationalErrors.FindIndex(error => error.Key == key);
        if (index >= 0)
        {
            _operationalErrors[index] = new OperationalErrorEntry(key, message);
        }
        else
        {
            _operationalErrors.Add(new OperationalErrorEntry(key, message));
        }
        RefreshOperationalErrorProjection();
    }

    private void ResolveOperationalError(string key)
    {
        _operationalErrors.RemoveAll(error => error.Key == key);
        RefreshOperationalErrorProjection();
    }

    [RelayCommand]
    private void DismissOperationalError()
    {
        if (_operationalErrors.Count > 0)
            _operationalErrors.RemoveAt(0);
        RefreshOperationalErrorProjection();
    }

    private void RefreshOperationalErrorProjection()
    {
        OperationalError = _operationalErrors.FirstOrDefault()?.Message ?? string.Empty;
        OperationalErrorCount = _operationalErrors.Count;
    }

    private void ReportProcessActionError(string path, string key, string message)
    {
        var pathKey = PathIdentity.Key(path);
        if (!_processActionErrors.TryGetValue(pathKey, out var errors))
        {
            errors = [];
            _processActionErrors[pathKey] = errors;
        }

        var index = errors.FindIndex(error => error.Key == key);
        if (index >= 0)
            errors[index] = new ProcessActionErrorEntry(key, message);
        else
            errors.Add(new ProcessActionErrorEntry(key, message));
        RefreshRecentActionErrorProjection();
    }

    private void ResolveProcessActionError(string path, string key)
    {
        var pathKey = PathIdentity.Key(path);
        if (_processActionErrors.TryGetValue(pathKey, out var errors))
        {
            errors.RemoveAll(error => error.Key == key);
            if (errors.Count == 0)
                _processActionErrors.Remove(pathKey);
        }
        RefreshRecentActionErrorProjection();
    }

    private void ResolveStoppedProcessActionErrors(string path)
    {
        RemoveProcessActionErrors(path, ["send-input", "stop"]);
    }

    private void ResolveReplacedProcessActionErrors(string path)
    {
        RemoveProcessActionErrors(path, ["send-input", "stop", "restart", "run", "read-output"]);
    }

    private void RemoveProcessActionErrors(string path, IReadOnlyCollection<string> keys)
    {
        var pathKey = PathIdentity.Key(path);
        if (_processActionErrors.TryGetValue(pathKey, out var errors))
        {
            errors.RemoveAll(error => keys.Contains(error.Key));
            if (errors.Count == 0)
                _processActionErrors.Remove(pathKey);
        }
        RefreshRecentActionErrorProjection();
    }

    [RelayCommand]
    private void DismissRecentActionError()
    {
        if (SelectedRecentEntry is not { } selected)
            return;

        var pathKey = PathIdentity.Key(selected.Path);
        if (_processActionErrors.TryGetValue(pathKey, out var errors) && errors.Count > 0)
        {
            errors.RemoveAt(0);
            if (errors.Count == 0)
                _processActionErrors.Remove(pathKey);
        }
        RefreshRecentActionErrorProjection();
    }

    private void RefreshRecentActionErrorProjection()
    {
        var pathKey = SelectedRecentEntry is { } selected ? PathIdentity.Key(selected.Path) : null;
        var errors = pathKey is not null && _processActionErrors.TryGetValue(pathKey, out var found)
            ? found
            : null;
        RecentActionError = errors?.FirstOrDefault()?.Message ?? string.Empty;
        RecentActionErrorCount = errors?.Count ?? 0;
    }

    private void EnsureRecentPath(string path)
    {
        if (_state.RecentlyRun.Any(run => PathIdentity.Same(run.Path, path)))
            return;
        _state.RecentlyRun = RecentRuns.Add(_state.RecentlyRun, path, DateTimeOffset.UtcNow);
    }

    private void SelectRecentPath(string path) =>
        SelectedRecentEntry = Recent.FirstOrDefault(entry => PathIdentity.Same(entry.Path, path));

    private void ShowCatalogResult(string text, bool transient = false)
    {
        _catalogResultTimer?.Stop();
        _catalogResultTimer = null;
        CatalogResult = text;
        if (!transient)
            return;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (ReferenceEquals(_catalogResultTimer, timer))
            {
                _catalogResultTimer = null;
                CatalogResult = string.Empty;
            }
        };
        _catalogResultTimer = timer;
        timer.Start();
    }

    private void ClearCatalogResult()
    {
        _catalogResultTimer?.Stop();
        _catalogResultTimer = null;
        CatalogResult = string.Empty;
    }

    // The Scripts-pane result after a scan: the deltas only ("3 new, 1 removed" / "Up to date"),
    // since the standing status bar already shows the total script count.
    private static string ScanResultMessage(ScanDiff diff)
    {
        var added = diff.Added.Count;
        var removed = diff.Removed.Count;
        if (added == 0 && removed == 0)
            return "Up to date.";
        if (removed == 0)
            return $"{added} new.";
        if (added == 0)
            return $"{removed} removed.";
        return $"{added} new, {removed} removed.";
    }
}
