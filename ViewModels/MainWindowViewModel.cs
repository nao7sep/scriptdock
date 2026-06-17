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
/// Root view model. Holds the durable config and volatile state, drives scanning through
/// <see cref="ScriptScanner"/> and launching through <see cref="ProcessRunner"/>, and
/// exposes the four lists and the console pane the window binds to. The list-building and
/// recent-run logic live in pure, tested helpers (<see cref="ScriptListBuilder"/>,
/// <see cref="RecentRuns"/>); the console polls the selected process's output.
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly IJsonStore<AppConfig> _configStore;
    private readonly IJsonStore<AppState> _stateStore;
    private readonly AppConfig _config;
    private readonly AppState _state;
    private readonly ScriptScanner _scanner;
    private readonly ProcessRunner _runner;

    // The most recent scan's outcome, kept so list rebuilds (after a favorite/hidden/show
    // toggle) preserve the new/removed flags until the next scan replaces them.
    private IReadOnlyList<string> _lastFound = [];
    private ISet<string> _newPaths = new HashSet<string>(StringComparer.Ordinal);
    private IReadOnlyList<string> _removed = [];

    private DispatcherTimer? _outputTimer;

    public ObservableCollection<ScriptItem> Scripts { get; } = [];
    public ObservableCollection<ScriptItem> Favorites { get; } = [];
    public ObservableCollection<ProcessItem> Processes { get; } = [];
    public ObservableCollection<RecentItem> Recent { get; } = [];

    [ObservableProperty]
    private bool _showHidden;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _status = "Ready.";

    [ObservableProperty]
    private ProcessItem? _selectedProcess;

    [ObservableProperty]
    private string _selectedOutput = string.Empty;

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
        _showHidden = state.ShowHidden; // set the field directly: no save/rebuild during construction

        _runner.ProcessesChanged += OnProcessesChanged;
    }

    /// <summary>The geometry to restore the window to, if any was saved.</summary>
    public WindowBounds? SavedBounds => _state.Window;

    /// <summary>Builds the recently-run list, starts the console poll, and runs the first scan.</summary>
    public async Task InitializeAsync()
    {
        RebuildRecent();
        StartOutputTimer();
        await RescanAsync();
    }

    /// <summary>Persists the window geometry so the next launch reopens here.</summary>
    public void PersistWindowBounds(double x, double y, double width, double height)
    {
        _state.Window = new WindowBounds { X = x, Y = y, Width = width, Height = height };
        _stateStore.Save(_state);
    }

    /// <summary>Stops the poll, terminates everything still running, and persists state.</summary>
    public void Shutdown()
    {
        _outputTimer?.Stop();
        _runner.ShutdownAll();
        _stateStore.Save(_state);
    }

    /// <summary>A fresh editable draft of the scan configuration for the settings dialog.</summary>
    public SettingsDialogViewModel CreateSettingsDraft() => new(_config);

    /// <summary>Applies an edited settings draft to the durable config and persists it. The
    /// new roots/extensions/patterns take effect on the next scan, so the user is nudged to
    /// rescan rather than the lists being silently rebuilt from a stale scan.</summary>
    public void ApplySettings(SettingsDialogViewModel draft)
    {
        _config.RootDirs = draft.RootDirs.ToList();
        _config.Extensions = draft.Extensions.ToList();
        _config.IgnorePatterns = draft.IgnorePatterns.ToList();
        _configStore.Save(_config);
        Status = "Configuration changed — Rescan to apply.";
    }

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

            RebuildLists();

            _state.KnownPaths = report.Found.ToList();
            _stateStore.Save(_state);

            Status = $"{report.Found.Count} script(s) — {diff.Added.Count} new, {diff.Removed.Count} removed.";
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>Runs the script — or, if it is already running, restarts it. The
    /// restart-like-crazy primitive: double-click a running script to relaunch it.</summary>
    [RelayCommand]
    private void Run(ScriptItem? item)
    {
        if (item is not null)
            RunByPath(item.Path);
    }

    [RelayCommand]
    private void RunRecent(RecentItem? item)
    {
        if (item is not null)
            RunByPath(item.Path);
    }

    [RelayCommand]
    private void ToggleFavorite(ScriptItem? item)
    {
        if (item is null)
            return;

        if (!_config.Favorites.Remove(item.Path))
            _config.Favorites.Add(item.Path);

        _configStore.Save(_config);
        RebuildLists();
    }

    [RelayCommand]
    private void ToggleHidden(ScriptItem? item)
    {
        if (item is null)
            return;

        if (!_config.Hidden.Remove(item.Path))
            _config.Hidden.Add(item.Path);

        _configStore.Save(_config);
        RebuildLists();
    }

    [RelayCommand]
    private void RestartProcess(ProcessItem? item)
    {
        if (item is not null)
            _runner.Restart(item.Process);
    }

    [RelayCommand]
    private void TerminateProcess(ProcessItem? item)
    {
        if (item is not null)
            _runner.Terminate(item.Process);
    }

    [RelayCommand]
    private void DismissProcess(ProcessItem? item)
    {
        if (item is not null)
            _runner.Dismiss(item.Process);
    }

    partial void OnShowHiddenChanged(bool value)
    {
        _state.ShowHidden = value;
        _stateStore.Save(_state);
        RebuildLists();
    }

    partial void OnSelectedProcessChanged(ProcessItem? value) => RefreshOutput();

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
    }

    private void OnProcessesChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(RebuildProcesses);

    private void RebuildProcesses()
    {
        var selectedId = SelectedProcess?.Id;

        foreach (var existing in Processes)
            existing.Dispose();
        Processes.Clear();

        foreach (var process in _runner.Active.OrderByDescending(p => p.StartedAt))
            Processes.Add(new ProcessItem(process));

        SelectedProcess = Processes.FirstOrDefault(p => p.Id == selectedId);
        RefreshOutput();
    }

    private void RebuildRecent()
    {
        Recent.Clear();
        foreach (var run in _state.RecentlyRun)
            Recent.Add(new RecentItem(run));
    }

    private void RebuildLists()
    {
        var favorites = new HashSet<string>(_config.Favorites, StringComparer.Ordinal);
        var hidden = new HashSet<string>(_config.Hidden, StringComparer.Ordinal);
        var foundSet = new HashSet<string>(_lastFound, StringComparer.Ordinal);

        Replace(Scripts, ScriptListBuilder.BuildScripts(_lastFound, _removed, favorites, hidden, _newPaths, ShowHidden));
        Replace(Favorites, ScriptListBuilder.BuildFavorites(_config.Favorites, foundSet));
    }

    private void StartOutputTimer()
    {
        if (_outputTimer is not null)
            return;

        _outputTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _outputTimer.Tick += (_, _) => RefreshOutput();
        _outputTimer.Start();
    }

    private void RefreshOutput()
    {
        var snapshot = SelectedProcess?.Process.Snapshot();
        SelectedOutput = snapshot is null ? string.Empty : string.Join(Environment.NewLine, snapshot);
    }

    private static void Replace(ObservableCollection<ScriptItem> target, IReadOnlyList<ScriptItem> items)
    {
        target.Clear();
        foreach (var item in items)
            target.Add(item);
    }
}
