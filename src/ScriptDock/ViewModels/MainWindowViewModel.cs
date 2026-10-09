using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScriptDock.I18n;
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
    private readonly IConfigStore _configStore;
    private readonly IJsonStore<AppState> _stateStore;
    private readonly IJsonStore<KnownPaths> _knownPathsStore;
    private readonly IRecordStore _records;
    private readonly AppConfig _config;
    private readonly AppState _state;
    private readonly KnownPaths _knownPaths;
    private readonly ScriptScanner _scanner;
    private readonly IProcessRunner _runner;

    // The most recent scan's outcome, kept so a hidden/show toggle preserves the new/removed flags. A
    // full scan replaces them and a background scan adds to them (ScanFlags); running a script or a
    // scan-settings change takes it out of the new set.
    private IReadOnlyList<string> _lastFound = [];
    private ISet<string> _newPaths = new HashSet<string>(StringComparer.Ordinal);
    private IReadOnlyList<string> _removed = [];

    // The "new" flags Run ended while the latest scan was saving its known paths; that scan's flags
    // leave them ended.
    private HashSet<string> _flagsEndedDuringScan = new(PathIdentity.Comparer);

    // The Recent list as read from the run and dismissal records, kept current in memory as each run or
    // dismissal is recorded. The version moves with every such change, so a read that a change overtook
    // is read again.
    private List<RecentRun> _recent = [];
    private int _recentVersion;

    private readonly List<ScriptProcess> _subscribed = [];
    private DispatcherTimer? _outputTimer;

    // Moves finished runs' output into the records now and then; one pass at a time, cancelled on shutdown.
    private static readonly TimeSpan OutputImportInterval = TimeSpan.FromMinutes(5);
    private DispatcherTimer? _outputImportTimer;
    private readonly CancellationTokenSource _outputImportCts = new();
    private Task _outputImport = Task.CompletedTask;

    // Cancels the in-flight scan when a newer scan supersedes it or the window closes, so a scan over
    // a slow/unresponsive root can never strand IsScanning (and thus the Rescan command) forever.
    private CancellationTokenSource? _scanCts;

    /// <summary>How long the Scripts pane waits after the main window comes back to the front before it
    /// rescans, so a burst of activations makes one scan.</summary>
    public static readonly TimeSpan ActivationRescanDelay = TimeSpan.FromMilliseconds(500);

    // Activation rescans start once the first scan has run and stop when the window shuts down. The
    // version moves with each activation, so only the latest one's wait ends in a scan.
    private bool _activationRescans;
    private ITimer? _activationRescanTimer;
    private int _activationRescanVersion;

    /// <summary>The latest activation rescan, for tests to await.</summary>
    internal Task ActivationScan { get; private set; } = Task.CompletedTask;

    // "Settings changed — Rescan to apply." is on screen, so the next completed scan answers it.
    private bool _rescanToApplyShown;

    public ObservableCollection<ScriptItem> Scripts { get; } = [];
    public ObservableCollection<RecentEntry> Recent { get; } = [];

    [ObservableProperty] private bool _showHidden;
    [ObservableProperty] private bool _isScanning;
    // Every line the window shows is held as a key and its values, and rendered where it is shown,
    // so a language change re-reads it instead of freezing the words it was built with.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CatalogResult))]
    [NotifyPropertyChangedFor(nameof(HasCatalogResult))]
    private Message? _catalogResultMessage;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OperationalError))]
    [NotifyPropertyChangedFor(nameof(HasOperationalError))]
    private Message? _operationalErrorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMultipleOperationalErrors))]
    [NotifyPropertyChangedFor(nameof(OperationalErrorCountText))]
    private int _operationalErrorCount;
    [ObservableProperty] private RecentEntry? _selectedRecentEntry;
    [ObservableProperty] private string _selectedOutput = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecentActionError))]
    [NotifyPropertyChangedFor(nameof(HasRecentActionError))]
    private Message? _recentActionErrorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMultipleRecentActionErrors))]
    [NotifyPropertyChangedFor(nameof(RecentActionErrorCountText))]
    private int _recentActionErrorCount;

    [ObservableProperty]
    private AutomationLiveSetting _recentActionLiveSetting = AutomationLiveSetting.Off;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRunning))]
    [NotifyPropertyChangedFor(nameof(RunningCountText))]
    private int _runningCount;

    // Status-bar persistent facts: total scripts found and how many of those are hidden.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScriptCountText))]
    private int _scriptCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHidden))]
    [NotifyPropertyChangedFor(nameof(HiddenCountText))]
    private int _hiddenCount;

    private readonly List<OperationalErrorEntry> _operationalErrors = [];
    private readonly Dictionary<string, List<ProcessActionErrorEntry>> _processActionErrors =
        new(PathIdentity.Comparer);
    private DispatcherTimer? _catalogResultTimer;
    private readonly HashSet<string> _scriptActions = new(PathIdentity.Comparer);
    private bool _quitting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleHiddenLabel))]
    private ScriptItem? _selectedScript;

    public MainWindowViewModel(
        IConfigStore configStore,
        IJsonStore<AppState> stateStore,
        IJsonStore<KnownPaths> knownPathsStore,
        IRecordStore records,
        AppConfig config,
        AppState state,
        KnownPaths knownPaths,
        ScriptScanner scanner,
        IProcessRunner runner)
    {
        _configStore = configStore;
        _stateStore = stateStore;
        _knownPathsStore = knownPathsStore;
        _records = records;
        _config = config;
        _state = state;
        _knownPaths = knownPaths;
        _scanner = scanner;
        _runner = runner;
        _showHidden = state.ShowHidden; // field, not property: no save/rebuild during construction

        ApplyUiFont();
        _runner.ProcessesChanged += (_, _) => Dispatcher.UIThread.Post(RebuildFromProcesses);
        _runner.RunEnded += (_, process) => _ = RecordRunEndAsync(RunEnd.For(_records.Session, process));

        // Everything this view model shows is held as a key, so a language change means every projection
        // has new words: an empty name tells the bindings to re-read them all.
        Localizer.Changed += OnLanguageChanged;
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

    public string OperationalError => Localizer.Current.Of(OperationalErrorMessage) ?? string.Empty;
    public string CatalogResult => Localizer.Current.Of(CatalogResultMessage) ?? string.Empty;
    public string RecentActionError => Localizer.Current.Of(RecentActionErrorMessage) ?? string.Empty;

    public bool HasOperationalError => OperationalErrorMessage is not null;
    public bool HasMultipleOperationalErrors => OperationalErrorCount > 1;
    public string OperationalErrorCountText => Localizer.T("error.count", ("count", OperationalErrorCount));
    public bool HasCatalogResult => CatalogResultMessage is not null;
    public bool HasRecentActionError => RecentActionErrorMessage is not null;
    public bool HasMultipleRecentActionErrors => RecentActionErrorCount > 1;
    public string RecentActionErrorCountText => Localizer.T("error.count", ("count", RecentActionErrorCount));

    /// <summary>The status bar's standing facts, each a count whose words follow its number.</summary>
    public string ScriptCountText => Localizer.T("status.scriptCount", ("count", ScriptCount));
    public string HiddenCountText => Localizer.T("status.hiddenCount", ("count", HiddenCount));
    public string RunningCountText => Localizer.T("status.runningCount", ("count", RunningCount));

    public double? SavedRecentWidth => _state.RecentPaneWidth;
    public double? SavedConsoleHeight => _state.ConsoleHeight;
    public int? WindowPositionX => _state.WindowPositionX;
    public int? WindowPositionY => _state.WindowPositionY;
    public double? WindowWidth => _state.WindowWidth;
    public double? WindowHeight => _state.WindowHeight;
    public bool WindowMaximized => _state.WindowMaximized;

    /// <summary>Drive the lists' empty-state messages. Refreshed after each rebuild.</summary>
    public bool NoScripts => Scripts.Count == 0;
    public bool NoRecent => Recent.Count == 0;

    /// <summary>Whether the console input field can send: the selected run is running. Re-evaluated
    /// whenever the selected entry changes.</summary>
    public bool CanSendInput => SelectedRecentEntry?.Process is { State: RunState.Running };

    /// <summary>Whether a Recent entry is selected — drives the Output header's script-name pill.</summary>
    public bool HasSelection => SelectedRecentEntry is not null;

    /// <summary>The Scripts pane's Hide/Show toggle label, reflecting the selected script's current
    /// state. Single-selection list, so the one button serves both directions (Hide a visible script,
    /// Show a hidden one). Defaults to "Hide" when nothing is selected.</summary>
    public string ToggleHiddenLabel =>
        Localizer.T(SelectedScript?.IsHidden == true ? "scripts.show" : "scripts.hide");

    /// <summary>Status-bar fact toggles: the running dot/segment and the hidden segment show only when non-zero.</summary>
    public bool HasRunning => RunningCount > 0;
    public bool HasHidden => HiddenCount > 0;

    /// <summary>Raised after running an input-accepting script, asking the view to focus the console
    /// input field so the user can type immediately. Focus is a view concern, so it is signalled here
    /// rather than performed.</summary>
    public event EventHandler? ConsoleInputFocusRequested;

    /// <summary>The saved theme. The view applies it app-wide (AppTheme) at startup and whenever
    /// Settings commits a change, which raises this property; the view model never touches the
    /// application.</summary>
    public ThemePreference Theme => _config.Theme;

    /// <summary>Raised after a saved UI-font change updates the dynamic app resource. The window
    /// owns measurement and native minimum sizing, so it remeasures after the new font lays out.</summary>
    public event EventHandler? UiFontChanged;

    /// <summary>Set by the view: whether one of its dialogs is open, which holds an activation rescan off.</summary>
    public Func<bool> IsDialogOpen { get; set; } = () => false;

    /// <summary>The clock the activation rescan and the quit's bounds wait on; tests pass their own.</summary>
    internal TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Set by the view to confirm a destructive action (the view owns the dialog). Returns
    /// true to proceed. Null when no view is attached (e.g. tests), in which case the action proceeds
    /// unconfirmed.</summary>
    public Func<ConfirmRequest, Task<bool>>? ConfirmHandler { get; set; }

    /// <summary>Whether quitting now would stop running work and so warrants a confirm: quitting stops
    /// every running script, so whenever something is running. The view drives the actual quit
    /// confirmation.</summary>
    public bool ShouldConfirmQuit() => RunningCount > 0;

    /// <summary>Starts the console poll, builds the Recent list, and runs the first scan.</summary>
    public async Task InitializeAsync()
    {
        StartOutputTimer();
        StartOutputImport();
        await LoadRecentAsync();
        RebuildRecent();
        await ScanAsync(background: false);
        _activationRescans = !_quitting;
    }

    /// <summary>
    /// The main window came back to the front: rescan the Scripts pane once activations settle, unless a
    /// scan is running or a dialog is open by then. The rescan is a quiet background scan: it keeps the
    /// flags already shown (ScanFlags) and the selection, and speaks only when it found a change.
    /// </summary>
    public void OnWindowActivated()
    {
        if (!_activationRescans)
            return;

        var version = ++_activationRescanVersion;
        _activationRescanTimer?.Dispose();
        _activationRescanTimer = Time.CreateTimer(
            _ => Dispatcher.UIThread.Post(() => RescanAfterActivation(version)),
            null, ActivationRescanDelay, Timeout.InfiniteTimeSpan);
    }

    private void RescanAfterActivation(int version)
    {
        if (version != _activationRescanVersion || !_activationRescans)
            return;
        _activationRescanTimer?.Dispose();
        _activationRescanTimer = null;

        // Run beside the Rescan command rather than through it, so Rescan and Cmd+R stay available and a
        // press supersedes this scan with a full one.
        if (!IsScanning && !IsDialogOpen())
            ActivationScan = ScanAsync(background: true);
    }

    internal async Task LoadRecentAsync()
    {
        try
        {
            int version;
            IReadOnlyList<RecentRun> recent;
            do
            {
                version = _recentVersion;
                recent = await _records.ReadRecentAsync();
            }
            while (version != _recentVersion);

            _recent = recent.ToList();
            ResolveOperationalError("recent");
        }
        catch (Exception ex)
        {
            Log.Error("ui: read recent runs failed", ex);
            ReportOperationalError("recent", Message.Of("guard.refresh"));
        }
    }

    // How long each step of a quit may take; together they stay well inside the time the system gives an
    // app at logout or shutdown (unsaved-edits-conventions, Quitting).
    internal static readonly TimeSpan QuitSettingsWriteBound = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan QuitStateSaveBound = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan QuitStopScriptsBound = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan QuitOutputImportBound = TimeSpan.FromMilliseconds(250);

    // The latest settings or hidden-list write and the settings it saves, so a quit can wait for it and
    // save it again.
    private AppConfig? _configWriteCandidate;
    private Task _configWrite = Task.CompletedTask;

    private Task SaveConfigAsync(AppConfig candidate)
    {
        _configWriteCandidate = candidate;
        return _configWrite = _configStore.SaveAsync(candidate);
    }

    /// <summary>
    /// Whether the user's own settings are safe for a quit. A settings or hidden-list change is adopted
    /// only once it is saved, and a failure is shown where it was made, so the one thing a quit can cut
    /// off is such a write still running; this waits for it within its bound. With
    /// <paramref name="retry"/> it saves that change again first. False when the change did not land: its
    /// write failed, or was still running at the bound.
    /// </summary>
    public async Task<bool> SettingsSavedForQuitAsync(bool retry = false)
    {
        var write = _configWrite;
        if (!retry && write.IsCompleted)
            return true; // landed, or failed before the quit and was reported where the change was made

        try
        {
            if (retry && _configWriteCandidate is { } candidate)
                write = SaveConfigAsync(candidate);
            await write.WaitAsync(QuitSettingsWriteBound, Time);
            return true;
        }
        catch (TimeoutException)
        {
            Log.Warn("quit: the settings change was still being saved at its bound",
                new { boundMs = QuitSettingsWriteBound.TotalMilliseconds });
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("quit: the settings change could not be saved", ex);
            return false;
        }
    }

    /// <summary>
    /// The quit's view-state save: the pane sizes with the window placement and the rest of the view
    /// state, within its bound. View state never holds a quit, so a failure is logged only.
    /// </summary>
    public Task PersistPaneSizesAsync(double recentWidth, double consoleHeight)
    {
        _state.RecentPaneWidth = recentWidth;
        _state.ConsoleHeight = consoleHeight;
        return QuitStepAsync("view state save", QuitStateSaveBound, () => _stateStore.SaveAsync(_state));
    }

    public void CaptureWindowPlacement(int x, int y, double width, double height, bool maximized)
    {
        _state.WindowPositionX = x;
        _state.WindowPositionY = y;
        _state.WindowWidth = width;
        _state.WindowHeight = height;
        _state.WindowMaximized = maximized;
    }

    public void BeginShutdown()
    {
        _quitting = true;
        _runner.SealLaunches();
        _scanCts?.Cancel();
    }

    /// <summary>
    /// Stops timers and scanning, stops every running script's process tree, and waits, each within its
    /// bound, for those trees and for the output import in flight — the window only finishes closing,
    /// and the app only exits, once this completes. The run output files stay for the next launch's import.
    /// </summary>
    public Task ShutdownAsync() => GuardAsync("shutdown", Message.Of("guard.shutdown"), async () =>
    {
        BeginShutdown();
        Localizer.Changed -= OnLanguageChanged;
        _outputTimer?.Stop();
        _outputImportTimer?.Stop();
        _catalogResultTimer?.Stop();
        _activationRescans = false;
        _activationRescanTimer?.Dispose();
        _scanCts?.Cancel(); // don't let a slow scan keep the closing window's work alive
        _outputImportCts.Cancel();
        await QuitStepAsync("stop running scripts", QuitStopScriptsBound, _runner.StopAllAsync);
        await QuitStepAsync("output import", QuitOutputImportBound, () => _outputImport);
    });

    // One step of a quit, within its bound. A failure, or a step still running at the bound, is logged and
    // the quit goes on; a step still running carries on and settles on its own, its outcome unknown.
    private async Task QuitStepAsync(string step, TimeSpan bound, Func<Task> work)
    {
        try
        {
            await work().WaitAsync(bound, Time);
        }
        catch (TimeoutException)
        {
            Log.Warn("quit: a step was still running at its bound; quitting without it",
                new { step, boundMs = bound.TotalMilliseconds });
        }
        catch (Exception ex)
        {
            Log.Error("quit: a step failed", ex, new { step });
        }
    }

    // Called on whichever thread saw the run end, so a failure is logged, not shown; the records' fallback
    // file keeps the end the database could not take.
    private async Task RecordRunEndAsync(RunEnd end)
    {
        try
        {
            await _records.AddRunEndAsync(end);
        }
        catch (Exception ex)
        {
            Log.Error("ui: record run end failed", ex, new { session = end.RunSession, run = end.Run });
        }
    }

    public SettingsDialogViewModel CreateSettingsDraft() => new(_config);

    /// <summary>True while there is no folder to scan, so nothing can be listed: the launch asks for one.</summary>
    public bool NeedsScanFolder => _config.RootDirs.Count == 0;

    /// <summary>
    /// First-run setup: adds the folder the user chose as a scan folder through the ordinary settings save,
    /// then scans it. False when the save failed; nothing changed then.
    /// </summary>
    public async Task<bool> AddFirstScanFolderAsync(string path)
    {
        var draft = CreateSettingsDraft();
        draft.AddPickedRootDir(path);
        if (!await TryApplySettingsAsync(draft))
            return false;
        await ScanAsync(background: false);
        return true;
    }

    /// <summary>
    /// The computer's own languages, in order, as they were read at launch. System resolves against
    /// these, so it cannot mean one language at launch and another after a change in Settings.
    /// </summary>
    internal IReadOnlyList<string> ComputerLanguages { get; init; } = [];

    private void OnLanguageChanged()
    {
        // An empty name tells every binding on this view model to re-read, and the Recent rows are
        // rebuilt because each row's state pill and time are words too.
        OnPropertyChanged(string.Empty);
        RebuildFromProcesses();
    }

    public async Task<bool> TryApplySettingsAsync(SettingsDialogViewModel draft)
    {
        var candidate = draft.ToConfig();
        candidate.Hidden = _config.Hidden.ToList();
        try
        {
            await SaveConfigAsync(candidate);
        }
        catch (Exception ex)
        {
            Log.Error("ui: apply settings failed", ex);
            return false;
        }

        var fontChanged = !string.Equals(_config.UiFontFamily, candidate.UiFontFamily, StringComparison.Ordinal);
        var scanChanged = !_config.RootDirs.SequenceEqual(candidate.RootDirs, StringComparer.Ordinal)
            || !_config.Extensions.SequenceEqual(candidate.Extensions, StringComparer.Ordinal)
            || !_config.IgnorePatterns.SequenceEqual(candidate.IgnorePatterns, StringComparer.Ordinal);
        _config.RootDirs = candidate.RootDirs;
        _config.Extensions = candidate.Extensions;
        _config.IgnorePatterns = candidate.IgnorePatterns;
        _config.UiFontFamily = candidate.UiFontFamily;
        var themeChanged = _config.Theme != candidate.Theme;
        _config.Theme = candidate.Theme;
        _config.Language = candidate.Language;
        if (themeChanged)
            OnPropertyChanged(nameof(Theme));

        // The language applies here, with its neighbours, and needs no restart: every surface holding a
        // key is re-read, and the view models re-render what they hold.
        Localizer.Use(candidate.Language, ComputerLanguages);
        ApplyUiFont();
        if (fontChanged)
            UiFontChanged?.Invoke(this, EventArgs.Empty);
        if (scanChanged)
            _scanCts?.Cancel();
        if (scanChanged && _newPaths.Count > 0)
        {
            // Changing what is scanned ends every "new" flag; the next scan flags against the new scope.
            _newPaths.Clear();
            RebuildScripts();
        }
        ShowCatalogResult(Message.Of("scan.configChanged"));
        _rescanToApplyShown = true;
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
                ReportProcessActionError(entry.Path, "send-input", Message.Of("process.sendInputFailed"));
            return sent;
        }
        catch (Exception ex)
        {
            Log.Error("ui: send input failed", ex);
            ReportProcessActionError(entry.Path, "send-input", Message.Of("process.sendInputFailed"));
            return false;
        }
    }

    [RelayCommand]
    private Task RescanAsync() => ScanAsync(background: false);

    // A full scan (launch, Rescan) says it is scanning, then how it ended, and replaces the flags. A
    // background scan (the window came back to the front) adds to the flags and says only what changed.
    private async Task ScanAsync(bool background)
    {
        if (_quitting)
            return;
        // Supersede any in-flight scan rather than refusing to start: a previous scan stuck on a
        // slow/unresponsive root must not disable Rescan forever. The latest scan owns IsScanning.
        _scanCts?.Cancel();
        var cts = new CancellationTokenSource();
        _scanCts = cts;
        var flagsEnded = new HashSet<string>(PathIdentity.Comparer);
        _flagsEndedDuringScan = flagsEnded;

        IsScanning = true;
        if (!background)
            ShowCatalogResult(Message.Of("scan.running"));
        try
        {
            var roots = _config.RootDirs.ToList();
            var extensions = _config.Extensions.ToList();
            var patterns = _config.IgnorePatterns.ToList();

            var report = await Task.Run(() => _scanner.Scan(roots, extensions, patterns, cts.Token), cts.Token);
            cts.Token.ThrowIfCancellationRequested(); // a newer scan superseded us between completion and here
            ScanReportLog.Write(_records, report);

            var diff = ScanDiff.Compute(report.Found, _knownPaths.Paths);
            var candidate = new KnownPaths { Paths = report.Found.ToList() };
            await _knownPathsStore.SaveAsync(candidate);
            cts.Token.ThrowIfCancellationRequested();

            // From the flags as they are now the save has settled, not as they were before it.
            var flags = background
                ? ScanFlags.Background(diff, report.Found, _newPaths, _removed)
                : ScanFlags.Full(diff);
            flags.NewKeys.ExceptWith(flagsEnded);

            _lastFound = report.Found;
            _newPaths = flags.NewKeys;
            _removed = flags.Removed;
            _knownPaths.Paths = candidate.Paths;
            RebuildScripts();
            ResolveOperationalError("scan");
            if (!background || diff.Added.Count > 0 || diff.Removed.Count > 0)
                ShowCatalogResult(ScanResultMessage(diff), transient: true);
            else if (_rescanToApplyShown)
                ClearCatalogResult(); // this scan applied the settings that line asked a Rescan for
            _rescanToApplyShown = false;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer scan (or cancelled on shutdown); that scan owns the status now.
        }
        catch (Exception ex)
        {
            Log.Error("ui: rescan failed", ex);
            if (cts.IsCancellationRequested || !ReferenceEquals(_scanCts, cts))
                return;
            if (!background)
                ClearCatalogResult(); // the "Scanning…" line
            ReportOperationalError("scan", Message.Of("scan.failed"));
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
        if (entry?.Process is not { State: RunState.Running } || _quitting)
            return;
        var key = entry.Process.ScriptKey;
        if (!_scriptActions.Add(key))
            return;

        try
        {
            // Stopping kills the live run, so confirm first.
            if (!await ConfirmAsync(
                Message.Of("stop.title"),
                Message.Of("stop.message", ("name", entry.DisplayName)),
                "stop.confirm"))
                return;

            if (!await _runner.TerminateAsync(entry.Process))
                ReportProcessActionError(entry.Path, "stop", Message.Of("process.stopFailed"));
            else
                ResolveStoppedProcessActionErrors(entry.Path);
        }
        catch (Exception ex)
        {
            Log.Error("ui: stop failed", ex, new { script = entry.Path });
            ReportProcessActionError(entry.Path, "stop", Message.Of("process.stopFailed"));
        }
        finally { _scriptActions.Remove(key); }
    }

    [RelayCommand]
    private async Task DismissEntry(RecentEntry? entry)
    {
        if (entry is null || _quitting)
            return;
        var key = entry.Process?.ScriptKey ?? PathIdentity.Key(entry.Path);
        if (!_scriptActions.Add(key))
            return;

        try
        {
            // Only dismissing a *running* entry destroys work, so confirm just that case; dismissing a
            // finished entry only drops it from the list (it can be re-run), so it stays immediate.
            if (entry.Process is { State: RunState.Running } &&
                !await ConfirmAsync(
                    Message.Of("dismiss.title"),
                    Message.Of("dismiss.message", ("name", entry.DisplayName)),
                    "dismiss.confirm"))
                return;

            // Capture the whole shown identity before releasing the live handle's stable key.
            var dismissedPaths = _recent.Where(r => PathIdentity.Comparer.Equals(ScriptKeyFor(r.Path), key))
                .Select(r => r.Path).Append(entry.Path).Append(key).ToHashSet(StringComparer.Ordinal);
            // Remember the dismissed row's position so focus lands on its neighbour, not nowhere.
            var index = Recent.IndexOf(entry);

            if (entry.Process is not null)
            {
                if (entry.Process.State == RunState.Running && !await _runner.TerminateAsync(entry.Process))
                {
                    ReportProcessActionError(entry.Path, "dismiss", Message.Of("process.dismissStopFailed"));
                    return;
                }
                _runner.Dismiss(entry.Process);
            }

            try
            {
                await Task.WhenAll(dismissedPaths.Select(_records.AddDismissalAsync));
            }
            catch (Exception) when (_records.DatabaseUnavailable)
            {
                // Kept in the fallback file; the dismissal holds for this session, as the startup notice said.
            }
            _recent.RemoveAll(r => dismissedPaths.Contains(r.Path));
            _recentVersion++;

            _processActionErrors.Remove(key);
            Log.Info("ui: dismiss", new { script = entry.Path });
            RebuildRecent();

            // The dismissed path is gone, so RebuildRecent cleared the selection; move it to the
            // neighbour at that position instead (the next entry, or the previous if it was last).
            SelectedRecentEntry = index < 0 || Recent.Count == 0 ? null : Recent[Math.Min(index, Recent.Count - 1)];
        }
        catch (Exception ex)
        {
            Log.Error("ui: dismiss failed", ex, new { script = entry.Path });
            ReportProcessActionError(entry.Path, "dismiss", Message.Of("process.dismissFailed"));
        }
        finally { _scriptActions.Remove(key); }
    }

    [RelayCommand]
    private async Task ToggleHidden(ScriptItem? item)
    {
        if (item is null)
            return;

        // The live config takes the new hidden list only once it is saved, so a failed save changes nothing.
        var nowHidden = !_config.Hidden.Any(path => PathIdentity.Same(path, item.Path));
        var hidden = nowHidden
            ? [.. _config.Hidden, item.Path]
            : _config.Hidden.Where(path => !PathIdentity.Same(path, item.Path)).ToList();
        var candidate = new AppConfig
        {
            UiFontFamily = _config.UiFontFamily,
            Language = _config.Language,
            Theme = _config.Theme,
            RootDirs = _config.RootDirs,
            Extensions = _config.Extensions,
            IgnorePatterns = _config.IgnorePatterns,
            Hidden = hidden,
        };

        try
        {
            await SaveConfigAsync(candidate);
            ResolveOperationalError("toggle hidden");
        }
        catch (Exception ex)
        {
            Log.Error("ui: toggle hidden failed", ex);
            ReportOperationalError("toggle hidden", Message.Of("guard.toggleHidden"));
            return;
        }

        _config.Hidden = hidden;
        Log.Info("ui: toggle hidden", new { script = item.Path, hidden = nowHidden });
        // Keep the toggled script selected; if hiding made it vanish (Show hidden off), fall to its
        // neighbour so the Scripts selection — and the Hide/Show label — never just resets.
        RebuildScripts(selectNeighbourIfGone: true);
    }

    partial void OnShowHiddenChanged(bool value)
    {
        _state.ShowHidden = value;
        // The visible list is a pure UI concern and does not wait on the disk: a slow or failing save
        // is reported as its own operational error without leaving the toggle's own effect stuck.
        ObserveSave(_stateStore.SaveAsync(_state), "show hidden", Message.Of("guard.showHidden"));
        RebuildScripts();
    }

    partial void OnSelectedRecentEntryChanged(RecentEntry? value)
    {
        OnPropertyChanged(nameof(CanSendInput));
        OnPropertyChanged(nameof(HasSelection));
        RefreshOutput();
        RefreshRecentActionErrorProjection();
    }

    private async Task RunByPath(string path, string displayName)
    {
        if (_quitting)
            return;
        var key = ScriptKeyFor(path);
        if (!_scriptActions.Add(key))
            return;
        try { await RunAdmittedAsync(path, displayName, key); }
        finally { _scriptActions.Remove(key); }
    }

    private async Task RunAdmittedAsync(string path, string displayName, string key)
    {
        ScriptProcess? started;
        try
        {
            var running = _runner.Active.FirstOrDefault(p =>
                PathIdentity.Comparer.Equals(p.ScriptKey, key) && p.State == RunState.Running);

            if (running is not null)
            {
                // Running an already-running script restarts it — that kills the live run, so confirm.
                if (!await ConfirmAsync(
                    Message.Of("restart.title"),
                    Message.Of("restart.message", ("name", displayName)),
                    "restart.confirm"))
                    return;
                if (_quitting)
                    return;
                started = await _runner.RestartAsync(running);
                if (_quitting && started is null)
                    return;
                if (started is null)
                {
                    ReportProcessActionError(path, "restart", Message.Of("process.restartFailed"));
                    RebuildRecent();
                    SelectRecentPath(path);
                    return;
                }
                ResolveProcessActionError(path, "restart");
            }
            else
            {
                started = await _runner.StartAsync(path);
                if (started is null)
                    return;
            }
        }
        catch (Exception ex)
        {
            // No run started, so nothing enters Recent: the failure shows on the script's existing
            // Recent row, or in the operational error bar when it has none.
            Log.Error("ui: run failed", ex, new { script = path });
            if (_quitting)
                return;
            RebuildRecent();
            if (Recent.Any(entry => PathIdentity.Same(entry.Path, path)))
            {
                SelectRecentPath(path);
                ReportProcessActionError(path, "run", FailurePresentation.ScriptStart(ex));
            }
            else
            {
                ReportOperationalError(RunFailedKey(path), FailurePresentation.ScriptStart(ex));
            }
            return;
        }

        ResolveOperationalError(RunFailedKey(path));

        // Running a script ends its "new" flag at once; otherwise the flag lasts until the next full scan.
        var flagKey = PathIdentity.Key(path);
        _flagsEndedDuringScan.Add(flagKey);
        if (_newPaths.Remove(flagKey))
            RebuildScripts();

        _recent = RecentRuns.Add(_recent, path, started.StartedAt);
        _recentVersion++;
        try
        {
            await _records.AddRunAsync(RunRecord.For(_records.Session, started));
        }
        catch (Exception ex)
        {
            Log.Error("ui: record run failed", ex, new { script = path });
            // With the database unavailable, the startup notice already said run history is not recorded.
            if (!_records.DatabaseUnavailable)
                ReportProcessActionError(path, "recent-history", Message.Of("process.historyFailed"));
        }
        RebuildRecent();

        // Surface the just-run script: select its Recent entry so its output shows in the console
        // immediately (selection re-pins the console to the bottom).
        SelectRecentPath(path);

        // The row is stable by path, but a successful Run/Restart produces a new process generation.
        // Failures tied to the prior process no longer have a surviving consequence on this row.
        ResolveReplacedProcessActionErrors(path);

        ResolveProcessActionError(path, "run");

        // A freshly started/restarted run owns a stdin pipe, so move keyboard focus to the console
        // input for immediate typing. Gated on CanSendInput so a non-input run never steals focus.
        if (CanSendInput)
            ConsoleInputFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RebuildFromProcesses()
    {
        Guard("refresh", Message.Of("guard.refresh"), () =>
        {
            RebuildRecent();
            RebuildScripts(); // refresh the tiles' running dots
        });
    }

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

        var selectedKey = SelectedRecentEntry is { } selected ? selected.Process?.ScriptKey ?? ScriptKeyFor(selected.Path) : null;

        Recent.Clear();
        foreach (var entry in RecentListBuilder.Build(_recent, _runner.Active, BuildLabels()))
            Recent.Add(entry);

        SelectedRecentEntry = selectedKey is null ? null : Recent.FirstOrDefault(e =>
            PathIdentity.Comparer.Equals(e.Process?.ScriptKey ?? ScriptKeyFor(e.Path), selectedKey));
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
        foreach (var run in _recent) paths.Add(run.Path);
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
            _runner.Active.Where(p => p.State == RunState.Running).Select(p => p.ScriptKey),
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

    private void StartOutputImport()
    {
        if (_outputImportTimer is null)
        {
            _outputImportTimer = new DispatcherTimer { Interval = OutputImportInterval };
            _outputImportTimer.Tick += (_, _) => StartOutputImport();
            _outputImportTimer.Start();
        }

        if (_outputImport.IsCompleted && !_outputImportCts.IsCancellationRequested)
            _outputImport = ImportFinishedOutputAsync();
    }

    private async Task ImportFinishedOutputAsync()
    {
        try
        {
            await _runner.ImportFinishedOutputAsync(_records, _outputImportCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Shutting down; the files stay for the next launch.
        }
        catch (Exception ex)
        {
            Log.Warn("run output: import failed; the files stay for the next pass", ex);
        }
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
                ReportProcessActionError(selected.Path, "read-output", Message.Of("process.readOutputFailed"));
        }
    }

    private void Guard(string action, Message failure, Action body)
    {
        try
        {
            body();
            ResolveOperationalError(action);
        }
        catch (Exception ex)
        {
            Log.Error($"ui: {action} failed", ex);
            ReportOperationalError(action, failure);
        }
    }

    // The async counterpart of Guard: awaits body() (rather than calling it inline) so an exception
    // raised at any point, before or after body's first await, is still caught and reported here
    // instead of becoming an unobserved task exception.
    private async Task GuardAsync(string action, Message failure, Func<Task> body)
    {
        try
        {
            await body();
            ResolveOperationalError(action);
        }
        catch (Exception ex)
        {
            Log.Error($"ui: {action} failed", ex);
            ReportOperationalError(action, failure);
        }
    }

    // Fire-and-forget a task already in flight (typically a queued store save), reporting failure the
    // same way GuardAsync does without making the caller await it. For saves that are last-writer-wins
    // snapshots rather than data a command result depends on.
    private void ObserveSave(Task task, string action, Message failure) => _ = GuardAsync(action, failure, () => task);

    // Ask the view to confirm a destructive action. With no handler attached (tests) there is no UI
    // to ask, so the action proceeds.
    private async Task<bool> ConfirmAsync(Message title, Message message, string confirmLabelKey) =>
        ConfirmHandler is null || await ConfirmHandler(new ConfirmRequest(title, message, confirmLabelKey));

    private sealed record OperationalErrorEntry(string Key, Message Message);
    private sealed record ProcessActionErrorEntry(string Key, Message Message);

    internal void ReportShellActionError(string key, Message message) => ReportOperationalError(key, message);

    internal void ResolveShellActionError(string key) => ResolveOperationalError(key);

    private void ReportOperationalError(string key, Message message)
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
        OperationalErrorMessage = _operationalErrors.FirstOrDefault()?.Message;
        OperationalErrorCount = _operationalErrors.Count;
    }

    private void ReportProcessActionError(string path, string key, Message message)
    {
        var pathKey = ScriptKeyFor(path);
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
        RefreshRecentActionErrorProjection(
            announce: SelectedRecentEntry is { } selected && PathIdentity.Comparer.Equals(selected.Process?.ScriptKey ?? ScriptKeyFor(selected.Path), pathKey));
    }

    private void ResolveProcessActionError(string path, string key)
    {
        var pathKey = ScriptKeyFor(path);
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
        var pathKey = ScriptKeyFor(path);
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

        var pathKey = selected.Process?.ScriptKey ?? ScriptKeyFor(selected.Path);
        if (_processActionErrors.TryGetValue(pathKey, out var errors) && errors.Count > 0)
        {
            errors.RemoveAt(0);
            if (errors.Count == 0)
                _processActionErrors.Remove(pathKey);
        }
        RefreshRecentActionErrorProjection();
    }

    private void RefreshRecentActionErrorProjection(bool announce = false)
    {
        var pathKey = SelectedRecentEntry is { } selected ? selected.Process?.ScriptKey ?? ScriptKeyFor(selected.Path) : null;
        var errors = pathKey is not null && _processActionErrors.TryGetValue(pathKey, out var found)
            ? found
            : null;
        RecentActionLiveSetting = announce && errors?.Count > 0
            ? AutomationLiveSetting.Assertive
            : AutomationLiveSetting.Off;
        RecentActionErrorMessage = errors?.FirstOrDefault()?.Message;
        RecentActionErrorCount = errors?.Count ?? 0;
    }

    private string ScriptKeyFor(string path) =>
        _runner.Active.FirstOrDefault(p => p.ScriptPath == path)?.ScriptKey ?? PathIdentity.Key(path);

    private string RunFailedKey(string path) => "run " + ScriptKeyFor(path);

    private void SelectRecentPath(string path) =>
        SelectedRecentEntry = Recent.FirstOrDefault(entry =>
            PathIdentity.Comparer.Equals(entry.Process?.ScriptKey ?? ScriptKeyFor(entry.Path), ScriptKeyFor(path)));

    private void ShowCatalogResult(Message text, bool transient = false)
    {
        _catalogResultTimer?.Stop();
        _catalogResultTimer = null;
        CatalogResultMessage = text;
        if (!transient)
            return;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (ReferenceEquals(_catalogResultTimer, timer))
            {
                _catalogResultTimer = null;
                CatalogResultMessage = null;
            }
        };
        _catalogResultTimer = timer;
        timer.Start();
    }

    private void ClearCatalogResult()
    {
        _catalogResultTimer?.Stop();
        _catalogResultTimer = null;
        CatalogResultMessage = null;
    }

    // The Scripts-pane result after a scan: the deltas only ("3 new, 1 removed" / "Up to date"),
    // since the standing status bar already shows the total script count.
    private static Message ScanResultMessage(ScanDiff diff)
    {
        var added = diff.Added.Count;
        var removed = diff.Removed.Count;
        if (added == 0 && removed == 0)
            return Message.Of("scan.upToDate");
        if (removed == 0)
            return Message.Of("scan.added", ("count", added));
        if (added == 0)
            return Message.Of("scan.removed", ("count", removed));

        // Two counted sentences, each inflected for its own number, joined by an entry that decides
        // the order and the separator. CLDR picks a form for one number, not two, so a single sentence
        // holding both counts would be wrong at "1 new, 1 removed" in English as much as anywhere else.
        return Message.Of("scan.addedAndRemoved",
            ("added", Message.Of("scan.added", ("count", added))),
            ("removed", Message.Of("scan.removed", ("count", removed))));
    }
}
