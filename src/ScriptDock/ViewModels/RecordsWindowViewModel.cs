using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
/// selected record whole. Every read takes its turn on the records' own thread, so reads end in the order
/// they were asked for; new filters cancel the list's reads for the old ones, a new selection the detail's,
/// and a cancelled read applies nothing. New records reach the list while it is open, at most once a
/// <see cref="LiveInterval"/>, without moving it under the reader.
/// </summary>
public sealed partial class RecordsWindowViewModel : ViewModelBase, IDisposable
{
    public static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan LiveInterval = TimeSpan.FromSeconds(1);

    private readonly IRecordReader _reader;
    private readonly IJsonStore<AppState> _stateStore;
    private readonly AppState _state;
    private readonly TimeProvider _time;
    private readonly Func<RunRecordDetail, Stream, bool> _openOutput;

    // The filters the list is read for; a page's own cursor is added per read.
    private RecordsQuery _filters = RecordsQuery.All with { NonZeroExitIsError = ExitCodes.NonZeroIsError };
    private bool _more;
    private RecordCursor? _loadedThrough;
    // New records are wanted: they arrived while the list was away from its top or busy reading.
    private bool _newestPending;
    // Cancelled when what they were read for is no longer asked for: the list's reads with each new set of
    // filters, the detail's with each new selection, and every read when the window closes.
    private CancellationTokenSource _listReads = new();
    private CancellationTokenSource _detailReads = new();
    private readonly CancellationTokenSource _windowReads = new();
    // A failed read is itself logged as a record, whose signal would start the next read; live reads stop
    // after a failure and resume once a read succeeds.
    private bool _liveSuspended;
    private bool _atTop = true;
    private ITimer? _liveTimer;
    private ITimer? _searchTimer;
    private bool _started;
    private bool IsClosed => _windowReads.IsCancellationRequested;

    // The rows are being replaced; the list's own selection writes during that are projections of it.
    private bool _syncingRows;
    private RecordRow? _selectedRecord;
    // The selection by key, which outlives the row object and a filter that hides it for a while.
    private string? _selectedKey;
    private RecordDetail? _detail;

    public RecordsWindowViewModel(
        IRecordReader reader,
        IJsonStore<AppState> stateStore,
        AppState state,
        TimeProvider? time = null,
        Func<RunRecordDetail, Stream, bool>? openOutput = null)
    {
        _reader = reader;
        _stateStore = stateStore;
        _state = state;
        _time = time ?? TimeProvider.System;
        _openOutput = openOutput ?? OpenInEditor;

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
    [ObservableProperty] private bool _openOutputFailed;
    [ObservableProperty] private bool _moreFailed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail), nameof(ShowNoSelection), nameof(ShowDetailFailed), nameof(ShowDetailLoading))]
    private RecordsDetailStatus _detailStatus = RecordsDetailStatus.None;

    public bool ShowLoadingNote => ListStatus == RecordsListStatus.Loading;
    public bool ShowFailedNote => ListStatus == RecordsListStatus.Failed;
    public bool ShowEmptyNote => ListStatus == RecordsListStatus.Ready && Rows.Count == 0;

    public bool HasDetail => DetailStatus == RecordsDetailStatus.Ready;
    public bool ShowNoSelection => DetailStatus == RecordsDetailStatus.None;
    public bool ShowDetailLoading => DetailStatus == RecordsDetailStatus.Loading;
    public bool ShowDetailFailed => DetailStatus == RecordsDetailStatus.Failed;

    public string DetailTitle => _detail switch
    {
        LogRecordDetail log => log.Message,
        RunRecordDetail run => run.Script,
        ScanReportRecordDetail scan => RecordFormat.ScanTitle(scan.Found),
        _ => string.Empty,
    };

    public bool DetailIsCodeTitle => _detail is not ScanReportRecordDetail;
    public bool DetailIsPathTitle => _detail is RunRecordDetail;
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
        if (_started || IsClosed)
            return;
        _started = true;
        _reader.Stored += OnStored;
        Localizer.Changed += OnLanguageChanged;
        DisplayTime.Changed += OnLanguageChanged; // every shown time is rendered again too
        ExitCodes.Changed += OnExitCodesChanged;
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
        if (IsClosed)
            return;
        _windowReads.Cancel();
        _listReads.Cancel();
        _detailReads.Cancel();
        _reader.Stored -= OnStored;
        Localizer.Changed -= OnLanguageChanged;
        DisplayTime.Changed -= OnLanguageChanged;
        ExitCodes.Changed -= OnExitCodesChanged;
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
        if (IsClosed || !ReferenceEquals(timer, _searchTimer))
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
        if (_started && !IsClosed)
            _ = ReloadAsync();
    }

    private CancellationToken Renew(ref CancellationTokenSource reads)
    {
        reads.Cancel();
        reads.Dispose();
        reads = CancellationTokenSource.CreateLinkedTokenSource(_windowReads.Token);
        return reads.Token;
    }

    private async Task ReloadAsync()
    {
        var token = Renew(ref _listReads);
        var query = _filters;
        _loadedThrough = null;
        _newestPending = false;
        _more = false;
        LoadingMore = false;
        MoreFailed = false;
        ListStatus = RecordsListStatus.Loading;
        SyncRows([]);

        RecordsPage page;
        try
        {
            page = await _reader.ReadRecordsPageAsync(query, token);
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested)
                return;
            ReadFailed(ex, "page");
            ListStatus = RecordsListStatus.Failed;
            return;
        }

        if (token.IsCancellationRequested)
            return;
        _liveSuspended = false;
        SyncRows(page.Records);
        _loadedThrough = RecordsPaging.CursorAfter(page.Records);
        _more = page.More;
        ListStatus = RecordsListStatus.Ready;
        if (_newestPending && _atTop)
            _ = ReadNewestAsync();
    }

    // The newest records are read once the list is ready; until then they stay wanted.
    private Task ReadNewestAsync()
    {
        if (IsClosed) return Task.CompletedTask;
        if (ListStatus == RecordsListStatus.Loading || LoadingMore)
        {
            _newestPending = true;
            return Task.CompletedTask;
        }
        _newestPending = false;
        return RefreshLoadedAsync();
    }

    // The loaded interval is authoritative: runs can leave a level filter after ending or enter a
    // search after output is imported. Keep the floor even if every visible row stops matching.
    private async Task RefreshLoadedAsync()
    {
        var token = _listReads.Token;
        var query = _filters with { Through = _loadedThrough };
        RecordsPage page;
        try
        {
            page = await _reader.ReadRecordsPageAsync(query, token);
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
                ReadFailed(ex, "newest");
            return;
        }

        if (token.IsCancellationRequested)
            return;
        if (!_atTop)
        {
            _newestPending = true;
            return;
        }
        _liveSuspended = false;
        SyncRows(page.Records);
        _loadedThrough ??= RecordsPaging.CursorAfter(page.Records);
        _more = page.More;
        ListStatus = RecordsListStatus.Ready;
    }

    // While a page is being read, LoadingMore refuses another; reads end in the order they were asked for, so
    // a refresh asked for earlier has applied before this page does.
    private async Task LoadMoreAsync()
    {
        if (IsClosed || ListStatus != RecordsListStatus.Ready || !_more || LoadingMore)
            return;
        var token = _listReads.Token;
        LoadingMore = true;
        MoreFailed = false;

        RecordsPage page;
        try
        {
            var query = _filters with { After = _loadedThrough };
            page = await _reader.ReadRecordsPageAsync(query, token);
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested)
                return;
            ReadFailed(ex, "next page");
            LoadingMore = false;
            MoreFailed = true;
            return;
        }

        if (token.IsCancellationRequested)
            return;
        _liveSuspended = false;
        var keys = Rows.Select(row => row.Key).ToHashSet(StringComparer.Ordinal);
        SyncRows([.. Summaries(), .. page.Records.Where(record => !keys.Contains(record.Key))]);
        _loadedThrough = RecordsPaging.CursorAfter(page.Records) ?? _loadedThrough;
        _more = page.More;
        LoadingMore = false;
        if (_newestPending && _atTop)
            _ = ReadNewestAsync();
    }

    private async Task ReadSourcesAsync()
    {
        RecordSources sources;
        try
        {
            sources = await _reader.ReadRecordSourcesAsync();
        }
        catch (Exception ex)
        {
            if (!IsClosed)
                ReadFailed(ex, "sources");
            return;
        }

        if (IsClosed)
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
        var token = Renew(ref _detailReads);
        if (summary is null)
        {
            ShowDetail(null, RecordsDetailStatus.None);
            return;
        }

        ShowDetail(null, RecordsDetailStatus.Loading);
        RecordDetail? detail;
        try
        {
            detail = await _reader.ReadRecordDetailAsync(summary.Kind, summary.Id, _filters.NonZeroExitIsError, token);
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested)
                return;
            Log.Warn("records: read failed", ex, new { read = "detail", kind = RecordKinds.Name(summary.Kind), id = summary.Id });
            ShowDetail(null, RecordsDetailStatus.Failed);
            return;
        }

        if (!token.IsCancellationRequested)
            ShowDetail(detail, detail is null ? RecordsDetailStatus.Failed : RecordsDetailStatus.Ready);
    }

    private void ShowDetail(RecordDetail? detail, RecordsDetailStatus status)
    {
        // A failed open belongs to its run: a refresh of the same run keeps it, another record clears it.
        if (detail is null || _detail is null || detail.Kind != _detail.Kind || detail.Id != _detail.Id)
            OpenOutputFailed = false;
        _detail = detail;
        BuildDetail();
        DetailStatus = status;
        OnPropertyChanged(nameof(CanOpenFullOutput));
        OnPropertyChanged(nameof(DetailTitle));
        OnPropertyChanged(nameof(DetailIsCodeTitle));
        OnPropertyChanged(nameof(DetailIsPathTitle));
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
        _outputPartial = false;
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
                Add("records.message", log.Message, code: true);
                Add("records.time", RecordFormat.Time(log.Time, milliseconds: true));
                Add("records.launch", RecordFormat.Launch(log.Session, _reader.Session));
                AddBlock("records.details", RecordFormat.LogDetails(log.Line));
                break;

            case RunRecordDetail run:
                Add("records.started", RecordFormat.Time(run.Time, milliseconds: true));
                Add("records.ended", run.EndedAt is null ? null : RecordFormat.Time(run.EndedAt, milliseconds: true));
                Add("records.endState", run.EndState, code: true);
                Add("records.exitCode", run.ExitCode?.ToString(Localizer.Current.Culture));
                Add("records.runId", Localizer.Current.Number(run.Run));
                Add("records.processId", run.Pid?.ToString(System.Globalization.CultureInfo.InvariantCulture), code: true);
                Add("records.processStarted", run.OsStartedAt is null ? null : RecordFormat.Time(run.OsStartedAt, milliseconds: true));
                Add("records.outputFile", run.OutputPath, code: true);
                Add("records.imported", run.ImportedAt is null ? null : RecordFormat.Time(run.ImportedAt, milliseconds: true));
                Add("records.launch", RecordFormat.Launch(run.Session, _reader.Session));
                if (RecordFormat.Output(run.Output, totalBytes: run.OutputLength) is { } output)
                {
                    // An output too large to keep in Records stays in its file, which search does not read.
                    string[] notes =
                    [
                        .. run.OutputKeptAsFile ? new[] { Localizer.T("records.outputKeptAsFile", ("size", Megabytes(run.OutputLength ?? 0))) } : [],
                        .. output.MoreBytes == 0 || run.OutputKeptAsFile ? [] : new[] { Localizer.T("records.outputMore", ("more", Megabytes(output.MoreBytes))) },
                        .. output.LinesShortened ? new[] { Localizer.T("records.outputLinesShortened") } : [],
                    ];
                    DetailBlocks.Add(new RecordBlock(
                        Localizer.T("records.output"),
                        output.Text,
                        notes.Length == 0 ? null : string.Join(" ", notes)));
                    _outputPartial = notes.Length > 0;
                }
                break;

            case ScanReportRecordDetail scan:
                Add("records.time", RecordFormat.Time(scan.Time, milliseconds: true));
                Add("records.launch", RecordFormat.Launch(scan.Session, _reader.Session));
                AddBlock("records.report", RecordFormat.PrettyJson(scan.Report));
                break;
        }
    }

    private bool _outputPartial;

    /// <summary>The detail shows the run's output only in part (its first 1 MB, or with long lines shortened), so
    /// Open full output is offered.</summary>
    public bool CanOpenFullOutput => DetailStatus == RecordsDetailStatus.Ready && _detail is RunRecordDetail && _outputPartial;

    /// <summary>
    /// Opens the selected run's whole stored output in the user's text editor: the direct path to what the
    /// detail's 1 MB view leaves out. The records copy it, in pieces, into a temporary file, which this then
    /// writes out as the console shows it and opens; the command stays disabled until it settles.
    /// </summary>
    [RelayCommand]
    private async Task OpenFullOutputAsync()
    {
        if (_detail is not RunRecordDetail run)
            return;

        OpenOutputFailed = false;
        bool opened;
        try
        {
            await using var output = new FileStream(Path.Combine(Path.GetTempPath(), "scriptdock-output-" + NanoId.New()),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
            var found = await _reader.ReadRunOutputAsync(run.Id, output);
            output.Position = 0;
            opened = found && await Task.Run(() => _openOutput(run, output));
            if (!found)
                Log.Error("records: run has no stored output to open", new { run = run.Id });
        }
        catch (Exception ex)
        {
            Log.Error("records: read full output failed", ex, new { run = run.Id });
            opened = false;
        }

        if (!opened && _detail is RunRecordDetail shown && shown.Id == run.Id)
            OpenOutputFailed = true;
    }

    [RelayCommand]
    private void DismissOpenOutputError() => OpenOutputFailed = false;

    private static bool OpenInEditor(RunRecordDetail run, Stream output)
    {
        string path;
        try
        {
            path = RunOutputFile.Write(StorageRoot.Directory, run.Script, run.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), output);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("records: write full output failed", ex, new { run = run.Id });
            return false;
        }
        return ExternalLauncher.Open(path);
    }

    // Rounded up to a tenth, so a few bytes left out never read as none.
    private static double Megabytes(long bytes) => Math.Ceiling(bytes / (1024.0 * 1024.0) * 10) / 10;

    // A selected run that has not settled is read again as records arrive, and shown again only when its
    // end or its output has arrived, so the pane is not rebuilt under the reader for nothing.
    private async Task RefreshRunDetailAsync(RunRecordDetail shown)
    {
        var token = _detailReads.Token;
        RecordDetail? detail;
        try
        {
            detail = await _reader.ReadRecordDetailAsync(shown.Kind, shown.Id, _filters.NonZeroExitIsError, token);
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
                ReadFailed(ex, "detail");
            return;
        }

        if (!token.IsCancellationRequested && detail is RunRecordDetail run && !run.SameProgress(shown))
            ShowDetail(run, RecordsDetailStatus.Ready);
    }

    private void OnStored() => Dispatcher.UIThread.Post(OnRecordStored);

    // A stored record reaches the list at once while it is scrolled to the top; otherwise it waits until
    // the list is back there, so the list never moves under the reader.
    private void OnRecordStored()
    {
        if (IsClosed || _liveTimer is not null || _liveSuspended)
            return;
        _liveTimer = _time.CreateTimer(_ => Dispatcher.UIThread.Post(LiveTick), null, LiveInterval, Timeout.InfiniteTimeSpan);
    }

    private void LiveTick()
    {
        _liveTimer?.Dispose();
        _liveTimer = null;
        if (IsClosed)
            return;

        if (_detail is RunRecordDetail { Settled: false } run)
            _ = RefreshRunDetailAsync(run);
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

    // A run's level is read under the setting, so the list is read again, a level filter's members
    // included, and a shown run's level with it.
    private void OnExitCodesChanged()
    {
        // Capture selection before Reload clears the backing rows. A pending detail has no _detail yet.
        var selected = SelectedRecord?.Summary;
        SetFilters(_filters with { NonZeroExitIsError = ExitCodes.NonZeroIsError });
        if (selected is not null) _ = ReadDetailAsync(selected);
        else if (_detail is RunRecordDetail run) _ = RefreshRunDetailAsync(run);
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
