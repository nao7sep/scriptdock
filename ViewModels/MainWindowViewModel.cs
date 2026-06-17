using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.Storage;

namespace ScriptDock.ViewModels;

/// <summary>
/// Root view model. Holds the durable config and volatile state, drives scanning through
/// <see cref="ScriptScanner"/> and launching through <see cref="ProcessRunner"/>, and
/// exposes the script and favorite lists the window binds to. The process and recently-run
/// lists and the output pane are wired in the next UI sub-step; the list-building and
/// recent-run logic live in pure, tested helpers (<see cref="ScriptListBuilder"/>,
/// <see cref="RecentRuns"/>).
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

    public ObservableCollection<ScriptItem> Scripts { get; } = [];
    public ObservableCollection<ScriptItem> Favorites { get; } = [];

    [ObservableProperty]
    private bool _showHidden;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _status = "Ready.";

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
    }

    /// <summary>Placeholder bound by the scaffold window until the real UI lands in the next sub-step.</summary>
    public string Placeholder =>
        $"ScriptDock — {_config.RootDirs.Count} root(s), {_config.Extensions.Count} extension(s) configured.";

    /// <summary>Runs the first scan; called from the window's Loaded handler.</summary>
    public Task InitializeAsync() => RescanAsync();

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

    /// <summary>Runs the script — or, if it is already running, restarts it. This is the
    /// restart-like-crazy primitive: double-click a running script to relaunch it.</summary>
    [RelayCommand]
    private void Run(ScriptItem? item)
    {
        if (item is null)
            return;

        var running = _runner.Active.FirstOrDefault(p =>
            string.Equals(p.ScriptPath, item.Path, StringComparison.Ordinal) && p.State == RunState.Running);

        if (running is not null)
            _runner.Restart(running);
        else
            _runner.Start(item.Path);

        _state.RecentlyRun = RecentRuns.Add(_state.RecentlyRun, item.Path, DateTimeOffset.UtcNow);
        _stateStore.Save(_state);
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

    partial void OnShowHiddenChanged(bool value)
    {
        _state.ShowHidden = value;
        _stateStore.Save(_state);
        RebuildLists();
    }

    private void RebuildLists()
    {
        var favorites = new HashSet<string>(_config.Favorites, StringComparer.Ordinal);
        var hidden = new HashSet<string>(_config.Hidden, StringComparer.Ordinal);
        var foundSet = new HashSet<string>(_lastFound, StringComparer.Ordinal);

        Replace(Scripts, ScriptListBuilder.BuildScripts(_lastFound, _removed, favorites, hidden, _newPaths, ShowHidden));
        Replace(Favorites, ScriptListBuilder.BuildFavorites(_config.Favorites, foundSet));
    }

    private static void Replace(ObservableCollection<ScriptItem> target, IReadOnlyList<ScriptItem> items)
    {
        target.Clear();
        foreach (var item in items)
            target.Add(item);
    }
}
