using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ScriptDock.I18n;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.Storage;

namespace ScriptDock.ViewModels;

public enum RecordsListStatus
{
    Loading,
    Failed,
    Ready,
}

public enum RecordsDetailStatus
{
    None,
    Loading,
    Failed,
    Ready,
}

/// <summary>
/// The Records window: a filtered list of the records, newest first, read a page at a time, beside the
/// selected record whole. Every read runs on the records' own thread and is bounded by
/// <see cref="ReadTimeout"/>; a result applies only while the filters it was read for are still the newest
/// asked for (PLAYBOOK, Own the work in flight). New records reach the list while it is open, at most once a
/// <see cref="LiveInterval"/>, without moving it under the reader.
/// </summary>
public sealed partial class RecordsWindowViewModel : ViewModelBase, IDisposable
{
    public static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan LiveInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(15);

    private readonly IRecordReader _reader;
    private readonly IJsonStore<AppState> _stateStore;
    private readonly AppState _state;
    private readonly TimeProvider _time;

    // The filters the list is read for; a page's own cursor is added per read.
    private RecordsQuery _filters = RecordsQuery.All;
    // Moves with every new set of filters, so a page read for older filters is dropped.
    private int _generation;
    private bool _more;
    // The busy claim for the next page, taken before the read starts.
    private bool _fetchingMore;
    // New records arrived while the list was scrolled away from its top.
    private bool _newestPending;
    // A failed read is itself logged as a record, whose signal would start the next read; live reads stop
    // after a failure and resume once a read succeeds.
    private bool _liveSuspended;
    private bool _atTop = true;
    private ITimer? _liveTimer;
    private ITimer? _searchTimer;
    private bool _started;
    private bool _disposed;

    // The rows are being replaced; the list's own selection writes during that are projections of it.
    private bool _syncingRows;
    private RecordRow? _selectedRecord;
    // The selection by key, which outlives the row object and a filter that hides it for a while.
    private string? _selectedKey;
    private int _detailVersion;
    private RecordDetail? _detail;

    public RecordsWindowViewModel(IRecordReader reader, IJsonStore<AppState> stateStore, AppState state, TimeProvider? time = null)
    {
        _reader = reader;
        _stateStore = stateStore;
        _state = state;
        _time = time ?? TimeProvider.System;

        LaunchOptions.Add(new RecordFilterOption(null, () => Localizer.T("records.allLaunches")));
        KindOptions =
        [
            new(null, () => Localizer.T("records.allKinds")),
            .. Enum.GetValues<RecordKind>().Select(kind => new RecordFilterOption(kind, () => RecordFormat.KindLabel(kind))),
        ];
        LevelOptions =
        [
            new(null, () => Localizer.T("records.allLevels")),
            .. Enum.GetValues<RecordLevelFilter>().Select(level => new RecordFilterOption(level, () => RecordFormat.LevelFilterLabel(level))),
        ];
        _selectedLaunch = LaunchOptions[0];
        _selectedKind = KindOptions[0];
        _selectedLevel = LevelOptions[0];
    }

    public ObservableCollection<RecordRow> Rows { get; } = [];
    public ObservableCollection<RecordFilterOption> LaunchOptions { get; } = [];
    public IReadOnlyList<RecordFilterOption> KindOptions { get; }
    public IReadOnlyList<RecordFilterOption> LevelOptions { get; }
    public ObservableCollection<RecordField> DetailFields { get; } = [];
    public ObservableCollection<RecordBlock> DetailBlocks { get; } = [];

    [ObservableProperty] private RecordFilterOption _selectedLaunch;
    [ObservableProperty] private RecordFilterOption _selectedKind;
    [ObservableProperty] private RecordFilterOption _selectedLevel;
    [ObservableProperty] private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLoadingNote), nameof(ShowFailedNote), nameof(ShowEmptyNote))]
    private RecordsListStatus _listStatus = RecordsListStatus.Loading;

    [ObservableProperty] private bool _loadingMore;
    [ObservableProperty] private bool _moreFailed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail), nameof(ShowNoSelection), nameof(ShowDetailFailed))]
    private RecordsDetailStatus _detailStatus = RecordsDetailStatus.None;

    public bool ShowLoadingNote => ListStatus == RecordsListStatus.Loading;
    public bool ShowFailedNote => ListStatus == RecordsListStatus.Failed;
    public bool ShowEmptyNote => ListStatus == RecordsListStatus.Ready && Rows.Count == 0;

    public bool HasDetail => DetailStatus == RecordsDetailStatus.Ready;
    public bool ShowNoSelection => DetailStatus == RecordsDetailStatus.None;
    public bool ShowDetailFailed => DetailStatus == RecordsDetailStatus.Failed;

    public string DetailTitle => _detail switch
    {
        LogRecordDetail log => log.Message,
        RunOutputRecordDetail run => run.Script ?? string.Empty,
        ScanReportRecordDetail scan => RecordFormat.ScanTitle(scan.Found),
        _ => string.Empty,
    };

    public bool DetailIsCodeTitle => _detail is not ScanReportRecordDetail;
    public string DetailLevelText => _detail is null ? string.Empty : RecordFormat.LevelLabel(_detail.Level);
    public bool DetailIsError => _detail?.Level == LogLevel.Error;
    public bool DetailIsWarning => _detail?.Level == LogLevel.Warn;
    public string DetailKindText => _detail is null ? string.Empty : RecordFormat.KindLabel(_detail.Kind);

    /// <summary>The list pane's saved width, healed into its bounds, which the window opens at.</summary>
    public double ListWidth => RecordsLayout.ClampListWidth(_state.RecordsListWidth);

    public int? WindowPositionX => _state.RecordsWindowPositionX;
    public int? WindowPositionY => _state.RecordsWindowPositionY;
    public double? WindowWidth => _state.RecordsWindowWidth;
    public double? WindowHeight => _state.RecordsWindowHeight;
    public bool WindowMaximized => _state.RecordsWindowMaximized;

    /// <summary>
    /// The selected row, as the list shows it. Selecting a row reads it whole; selecting the last row
    /// loaded reads the next page.
    /// </summary>
    public RecordRow? SelectedRecord
    {
        get => _selectedRecord;
        set
        {
            if (_syncingRows || ReferenceEquals(value, _selectedRecord))
                return;

            _selectedRecord = value;
            OnPropertyChanged();
            if (value?.Key != _selectedKey)
            {
                _selectedKey = value?.Key;
                _ = ReadDetailAsync(value?.Summary);
            }

            if (value is not null && Rows.Count > 0 && ReferenceEquals(Rows[^1], value))
                LoadMore();
        }
    }

    /// <summary>Begins listening for new records and reads the first page.</summary>
    public void Start()
    {
        if (_started || _disposed)
            return;
        _started = true;
        _reader.Stored += OnStored;
        Localizer.Changed += OnLanguageChanged;
        _ = ReadSourcesAsync();
        _ = ReloadAsync();
    }

    /// <summary>Reads the next page, unless one is being read or there is none; a failed page is read again.</summary>
    public void LoadMore() => _ = LoadMoreAsync();

    /// <summary>The reader scrolled the list. Reaching the top shows records that arrived meanwhile;
    /// nearing the end reads the next page, again after a failed one.</summary>
    public void ListScrolled(bool atTop, bool nearEnd)
    {
        _atTop = atTop;
        if (_newestPending && atTop)
        {
            _newestPending = false;
            _ = ReadNewestAsync();
        }

        if (nearEnd)
            LoadMore();
    }

    /// <summary>The list was laid out again. A page that leaves it short of its end reads the next one; a
    /// failed page waits for the reader instead.</summary>
    public void ListLaidOut(bool atTop, bool nearEnd)
    {
        _atTop = atTop;
        if (nearEnd && ListStatus == RecordsListStatus.Ready && !LoadingMore && !MoreFailed)
            LoadMore();
    }

    /// <summary>Keeps the width a splitter drag ended at as the list's width.</summary>
    public Task CommitListWidthAsync(double width)
    {
        _state.RecordsListWidth = RecordsLayout.ClampListWidth(width);
        return SaveStateAsync("list width");
    }

    /// <summary>Keeps the window's normal rectangle, and on Windows whether it is maximized, at its close.</summary>
    public Task SavePlacementAsync(int x, int y, double width, double height, bool maximized)
    {
        _state.RecordsWindowPositionX = x;
        _state.RecordsWindowPositionY = y;
        _state.RecordsWindowWidth = width;
        _state.RecordsWindowHeight = height;
        _state.RecordsWindowMaximized = maximized;
        return SaveStateAsync("placement");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _generation++;
        _detailVersion++;
        _reader.Stored -= OnStored;
        Localizer.Changed -= OnLanguageChanged;
        _liveTimer?.Dispose();
        _searchTimer?.Dispose();
    }

    // A filter's list never empties its choice on purpose; a null from it leaves the filters as they are.
    partial void OnSelectedLaunchChanged(RecordFilterOption value)
    {
        if (value is not null)
            SetFilters(_filters with { Session = value.Value as string });
    }

    partial void OnSelectedKindChanged(RecordFilterOption value)
    {
        if (value is not null)
            SetFilters(_filters with { Kind = value.Value as RecordKind? });
    }

    partial void OnSelectedLevelChanged(RecordFilterOption value)
    {
        if (value is not null)
            SetFilters(_filters with { Level = value.Value as RecordLevelFilter? });
    }

    // Search applies once typing pauses.
    partial void OnSearchTextChanged(string value)
    {
        _searchTimer?.Dispose();
        ITimer? timer = null;
        timer = _time.CreateTimer(_ => Dispatcher.UIThread.Post(() => ApplySearch(timer!)), null, SearchDelay, Timeout.InfiniteTimeSpan);
        _searchTimer = timer;
    }

    private void ApplySearch(ITimer timer)
    {
        if (_disposed || !ReferenceEquals(timer, _searchTimer))
            return;
        _searchTimer = null;
        timer.Dispose();
        SetFilters(_filters with { Search = SearchText });
    }

    private void SetFilters(RecordsQuery filters)
    {
        if (filters == _filters)
            return;
        _filters = filters;
        if (_started && !_disposed)
            _ = ReloadAsync();
    }

    private Task<T> Bounded<T>(Task<T> read) => read.WaitAsync(ReadTimeout, _time);

    private async Task ReloadAsync()
    {
        var generation = ++_generation;
        var query = _filters;
        _fetchingMore = false;
        _newestPending = false;
        _more = false;
        LoadingMore = false;
        MoreFailed = false;
        ListStatus = RecordsListStatus.Loading;
        SyncRows([]);

        RecordsPage page;
        try
        {
            page = await Bounded(_reader.ReadRecordsPageAsync(query));
        }
        catch (Exception ex)
        {
            if (generation != _generation)
                return;
            ReadFailed(ex, "page");
            ListStatus = RecordsListStatus.Failed;
            return;
        }

        if (generation != _generation)
            return;
        _liveSuspended = false;
        SyncRows(page.Records);
        _more = page.More;
        ListStatus = RecordsListStatus.Ready;
    }

    // The newest page read again for new records. It joins the rows already shown rather than replacing
    // them, so the list never falls back to its loading note and the pages already read stay.
    private async Task ReadNewestAsync()
    {
        var generation = _generation;
        var query = _filters;
        RecordsPage page;
        try
        {
            page = await Bounded(_reader.ReadRecordsPageAsync(query));
        }
        catch (Exception ex)
        {
            if (generation == _generation)
                ReadFailed(ex, "newest");
            return;
        }

        if (generation != _generation)
            return;
        _liveSuspended = false;
        if (ListStatus == RecordsListStatus.Ready)
        {
            var (records, more) = RecordsPaging.MergeNewestPage(Summaries(), _more, page);
            SyncRows(records);
            _more = more;
            return;
        }

        SyncRows(page.Records);
        _more = page.More;
        LoadingMore = false;
        MoreFailed = false;
        ListStatus = RecordsListStatus.Ready;
    }

    private async Task LoadMoreAsync()
    {
        if (_disposed || ListStatus != RecordsListStatus.Ready || !_more || _fetchingMore)
            return;
        _fetchingMore = true;
        var generation = _generation;
        var shown = Summaries();
        var query = _filters with { After = RecordsPaging.CursorAfter(shown) };
        LoadingMore = true;
        MoreFailed = false;

        RecordsPage page;
        try
        {
            page = await Bounded(_reader.ReadRecordsPageAsync(query));
        }
        catch (Exception ex)
        {
            if (generation != _generation)
                return;
            _fetchingMore = false;
            ReadFailed(ex, "next page");
            LoadingMore = false;
            MoreFailed = true;
            return;
        }

        if (generation != _generation)
            return;
        _fetchingMore = false;
        _liveSuspended = false;
        var keys = Rows.Select(row => row.Key).ToHashSet(StringComparer.Ordinal);
        SyncRows([.. Summaries(), .. page.Records.Where(record => !keys.Contains(record.Key))]);
        _more = page.More;
        LoadingMore = false;
    }

    private async Task ReadSourcesAsync()
    {
        RecordSources sources;
        try
        {
            sources = await Bounded(_reader.ReadRecordSourcesAsync());
        }
        catch (Exception ex)
        {
            if (!_disposed)
                ReadFailed(ex, "sources");
            return;
        }

        if (_disposed)
            return;

        // Launches are only ever added, newest first; the choices already offered stay as they are.
        var offered = LaunchOptions.Skip(1).Select(option => (string)option.Value!).ToHashSet(StringComparer.Ordinal);
        var index = 1;
        foreach (var session in sources.Sessions)
        {
            if (!offered.Contains(session))
                LaunchOptions.Insert(index, new RecordFilterOption(session, () => RecordFormat.Launch(session, _reader.Session)));
            index = LaunchOptions.IndexOf(LaunchOptions.First(option => Equals(option.Value, session))) + 1;
        }
    }

    private async Task ReadDetailAsync(RecordSummary? summary)
    {
        var version = ++_detailVersion;
        if (summary is null)
        {
            ShowDetail(null, RecordsDetailStatus.None);
            return;
        }

        ShowDetail(null, RecordsDetailStatus.Loading);
        RecordDetail? detail;
        try
        {
            detail = await Bounded(_reader.ReadRecordDetailAsync(summary.Kind, summary.Id));
        }
        catch (Exception ex)
        {
            if (version != _detailVersion)
                return;
            Log.Warn("records: read failed", ex, new { read = "detail", kind = RecordKinds.Name(summary.Kind), id = summary.Id });
            ShowDetail(null, RecordsDetailStatus.Failed);
            return;
        }

        if (version == _detailVersion)
            ShowDetail(detail, detail is null ? RecordsDetailStatus.Failed : RecordsDetailStatus.Ready);
    }

    private void ShowDetail(RecordDetail? detail, RecordsDetailStatus status)
    {
        _detail = detail;
        BuildDetail();
        DetailStatus = status;
        OnPropertyChanged(nameof(DetailTitle));
        OnPropertyChanged(nameof(DetailIsCodeTitle));
        OnPropertyChanged(nameof(DetailLevelText));
        OnPropertyChanged(nameof(DetailIsError));
        OnPropertyChanged(nameof(DetailIsWarning));
        OnPropertyChanged(nameof(DetailKindText));
    }

    // Every field the record holds, under its label; a field the record does not have, and a block with
    // nothing in it, are left out.
    private void BuildDetail()
    {
        DetailFields.Clear();
        DetailBlocks.Clear();
        if (_detail is null)
            return;

        void Add(string key, string? value, bool code = false)
        {
            if (value is not null)
                DetailFields.Add(new RecordField(Localizer.T(key), value, code));
        }

        void AddBlock(string key, string? text)
        {
            if (text is not null)
                DetailBlocks.Add(new RecordBlock(Localizer.T(key), text));
        }

        switch (_detail)
        {
            case LogRecordDetail log:
                Add("records.time", RecordFormat.Time(log.Time, milliseconds: true));
                Add("records.launch", RecordFormat.Launch(log.Session, _reader.Session));
                AddBlock("records.details", RecordFormat.LogDetails(log.Line));
                break;

            case RunOutputRecordDetail run:
                Add("records.started", run.StartedAt is null ? null : RecordFormat.Time(run.StartedAt, milliseconds: true));
                Add("records.ended", run.EndedAt is null ? null : RecordFormat.Time(run.EndedAt, milliseconds: true));
                Add("records.endState", run.EndState, code: true);
                Add("records.exitCode", run.ExitCode?.ToString(Localizer.Current.Culture));
                Add("records.runId", Localizer.Current.Number(run.Run));
                Add("records.processId", run.Pid?.ToString(System.Globalization.CultureInfo.InvariantCulture), code: true);
                Add("records.processStarted", run.OsStartedAt is null ? null : RecordFormat.Time(run.OsStartedAt, milliseconds: true));
                Add("records.outputFile", run.OutputPath, code: true);
                Add("records.imported", RecordFormat.Time(run.Time, milliseconds: true));
                Add("records.launch", RecordFormat.Launch(run.Session, _reader.Session));
                AddBlock("records.output", RecordFormat.OutputText(run.Output));
                break;

            case ScanReportRecordDetail scan:
                Add("records.time", RecordFormat.Time(scan.Time, milliseconds: true));
                Add("records.launch", RecordFormat.Launch(scan.Session, _reader.Session));
                AddBlock("records.report", RecordFormat.PrettyJson(scan.Report));
                break;
        }
    }

    private void OnStored() => Dispatcher.UIThread.Post(OnRecordStored);

    // A stored record reaches the list at once while it is scrolled to the top; otherwise it waits until
    // the list is back there, so the list never moves under the reader.
    private void OnRecordStored()
    {
        if (_disposed || _liveTimer is not null || _liveSuspended)
            return;
        _liveTimer = _time.CreateTimer(_ => Dispatcher.UIThread.Post(LiveTick), null, LiveInterval, Timeout.InfiniteTimeSpan);
    }

    private void LiveTick()
    {
        _liveTimer?.Dispose();
        _liveTimer = null;
        if (_disposed)
            return;

        _ = ReadSourcesAsync();
        if (_atTop)
            _ = ReadNewestAsync();
        else
            _newestPending = true;
    }

    // Logged, not shown: the list's own note says the read failed. The log line is a record, so live reads
    // are suspended first.
    private void ReadFailed(Exception ex, string read)
    {
        _liveSuspended = true;
        Log.Warn("records: read failed", ex, new { read });
    }

    private List<RecordSummary> Summaries() => Rows.Select(row => row.Summary).ToList();

    // The rows become exactly the given summaries, newest first. A row whose summary is unchanged keeps its
    // object, so the list keeps its selection and scroll position; the selection is then restored by key.
    private void SyncRows(IReadOnlyList<RecordSummary> desired)
    {
        _syncingRows = true;
        try
        {
            var existing = new Dictionary<string, RecordRow>(StringComparer.Ordinal);
            foreach (var row in Rows)
                existing[row.Key] = row;

            var target = desired
                .Select(summary => existing.TryGetValue(summary.Key, out var row) && row.Summary == summary ? row : new RecordRow(summary))
                .ToList();
            var kept = new HashSet<RecordRow>(target, ReferenceEqualityComparer.Instance);
            for (var index = Rows.Count - 1; index >= 0; index--)
            {
                if (!kept.Contains(Rows[index]))
                    Rows.RemoveAt(index);
            }

            for (var index = 0; index < target.Count; index++)
            {
                if (index >= Rows.Count || !ReferenceEquals(Rows[index], target[index]))
                    Rows.Insert(index, target[index]);
            }
        }
        finally
        {
            _syncingRows = false;
        }

        _selectedRecord = _selectedKey is null ? null : Rows.FirstOrDefault(row => row.Key == _selectedKey);
        OnPropertyChanged(nameof(SelectedRecord));
        OnPropertyChanged(nameof(ShowEmptyNote));
    }

    private void OnLanguageChanged()
    {
        foreach (var row in Rows)
            row.Refresh();
        foreach (var option in LaunchOptions.Concat(KindOptions).Concat(LevelOptions))
            option.Refresh();
        BuildDetail();
        OnPropertyChanged(string.Empty);
    }

    private async Task SaveStateAsync(string what)
    {
        try
        {
            await _stateStore.SaveAsync(_state);
        }
        catch (Exception ex)
        {
            Log.Warn("records window: state save failed", ex, new { what });
        }
    }
}
