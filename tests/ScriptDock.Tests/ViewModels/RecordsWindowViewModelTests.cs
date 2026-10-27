using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.Tests.Fakes;
using ScriptDock.Tests.I18n;
using ScriptDock.ViewModels;
using Xunit;

namespace ScriptDock.Tests.ViewModels;

/// <summary>
/// The Records window's behaviour, read through its view model with the reads and the clock in hand:
/// paging, filters, the notes, live updates and the state it keeps. Each runs on the UI thread, where the
/// view model posts its signals and read results.
/// </summary>
public sealed class RecordsWindowViewModelTests
{
    private const string Session = FakeRecordReader.CurrentSession;

    private static readonly RecordSummary Failed = new(
        RecordKind.Run, 4, Session, "2026-10-04T08:01:00.000Z", LogLevel.Error, "/repo/dev.command", null, null);
    private static readonly RecordSummary Warning = new(
        RecordKind.Log, 9, Session, "2026-10-04T08:00:30.000Z", LogLevel.Warn, "run: terminate failed", null, null);
    private static readonly RecordSummary Newer = new(
        RecordKind.Log, 12, Session, "2026-10-04T08:02:00.000Z", LogLevel.Info, "app.later", null, null);
    private static readonly RecordSummary Scan = new(
        RecordKind.ScanReport, 2, Session, "2026-10-04T08:00:00.000Z", LogLevel.Info, "", """["/code","/work"]""", 3);

    private readonly FakeRecordReader _reader = new() { Page = new RecordsPage([Failed, Warning], false) };
    private readonly FakeJsonStore<AppState> _stateStore = new();
    private readonly FakeTimeProvider _time = new();

    private RecordsWindowViewModel Started()
    {
        var vm = new RecordsWindowViewModel(_reader, _stateStore, _stateStore.Value, _time);
        vm.Start();
        Pump();
        return vm;
    }

    private static void Pump() => Dispatcher.UIThread.RunJobs();

    private void Advance(TimeSpan by)
    {
        _time.Advance(by);
        Pump();
    }

    private static string[] Titles(RecordsWindowViewModel vm) => vm.Rows.Select(row => row.Summary.Title).ToArray();

    private static RecordCursor CursorOf(RecordSummary record) => new(record.Time, record.Kind, record.Id);

    [AvaloniaFact]
    public void PendingDetailShowsLoadingUntilTheCurrentSelectionSettles()
    {
        var pending = new TaskCompletionSource<RecordDetail?>();
        _reader.Detail = (_, _) => pending.Task;
        using var vm = Started();
        vm.SelectedRecord = vm.Rows[0];
        Assert.True(vm.ShowDetailLoading);
        Assert.False(vm.ShowNoSelection);
        vm.SelectedRecord = null;
        Assert.False(vm.ShowDetailLoading);
        Assert.True(vm.ShowNoSelection);
        pending.SetResult(RunDetail());
        Pump();
        Assert.False(vm.HasDetail);
    }

    [AvaloniaFact]
    public void Lists_the_records_newest_first_with_every_filter_off_and_nothing_selected()
    {
        var vm = Started();

        Assert.Equal(["/repo/dev.command", "run: terminate failed"], Titles(vm));
        Assert.Equal(RecordsQuery.All, _reader.LastQuery);
        Assert.Null(vm.SelectedRecord);
        Assert.True(vm.ShowNoSelection);
        Assert.Same(vm.LaunchOptions[0], vm.SelectedLaunch);
        Assert.Same(vm.KindOptions[0], vm.SelectedKind);
        Assert.Same(vm.LevelOptions[0], vm.SelectedLevel);
    }

    [AvaloniaFact]
    public void Shows_a_loading_note_while_the_first_page_is_read_then_the_rows()
    {
        var first = new TaskCompletionSource<RecordsPage>();
        _reader.Next(first.Task);
        var vm = Started();

        Assert.True(vm.ShowLoadingNote);
        Assert.False(vm.ShowEmptyNote);
        Assert.Empty(vm.Rows);

        first.SetResult(new RecordsPage([Failed, Warning], false));
        Pump();

        Assert.False(vm.ShowLoadingNote);
        Assert.Equal(2, vm.Rows.Count);
    }

    [AvaloniaFact]
    public void Says_so_when_no_record_matches_and_when_the_records_cannot_be_read()
    {
        _reader.Next(new RecordsPage([], false));
        var vm = Started();
        Assert.True(vm.ShowEmptyNote);
        Assert.False(vm.ShowFailedNote);

        _reader.NextFails();
        vm.SelectedLevel = vm.LevelOptions[1];
        Pump();
        Assert.True(vm.ShowFailedNote);
        Assert.False(vm.ShowEmptyNote);
        Assert.False(vm.ShowLoadingNote);
    }

    [AvaloniaFact]
    public void Offers_needs_attention_first_among_the_levels()
    {
        var vm = new RecordsWindowViewModel(_reader, _stateStore, _stateStore.Value, _time);

        Assert.Equal(
            new[] { "records.allLevels", "records.levelAttention", "records.levelError", "records.levelWarn", "records.levelInfo", "records.levelDebug" }
                .Select(key => English.Of(key)),
            vm.LevelOptions.Select(option => option.Label));
        Assert.Equal(
            new[] { "records.allKinds", "records.kindLog", "records.kindRun", "records.kindScanReport" }.Select(key => English.Of(key)),
            vm.KindOptions.Select(option => option.Label));
    }

    [AvaloniaFact]
    public void Reads_again_with_each_filter_and_searches_once_typing_pauses()
    {
        _reader.Sources = () => Task.FromResult(new RecordSources(Session, [Session, "2026-10-03T08:00:00.000Z"]));
        var vm = Started();
        Assert.Equal(3, vm.LaunchOptions.Count);
        Assert.Contains(English.Of("records.thisLaunch", ("time", "")).Trim(), vm.LaunchOptions[1].Label);
        Assert.DoesNotContain(English.Of("records.thisLaunch", ("time", "")).Trim(), vm.LaunchOptions[2].Label);

        vm.SelectedLaunch = vm.LaunchOptions[1];
        vm.SelectedKind = vm.KindOptions.Single(option => Equals(option.Value, RecordKind.Run));
        vm.SelectedLevel = vm.LevelOptions.Single(option => Equals(option.Value, RecordLevelFilter.Attention));
        Pump();
        Assert.Equal(new RecordsQuery(Session, RecordKind.Run, RecordLevelFilter.Attention, "", null), _reader.LastQuery);

        var reads = _reader.PageQueries.Count;
        vm.SearchText = "quo";
        Advance(TimeSpan.FromMilliseconds(200));
        vm.SearchText = "quota";
        Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(reads, _reader.PageQueries.Count);

        Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(reads + 1, _reader.PageQueries.Count);
        Assert.Equal("quota", _reader.LastQuery.Search);
    }

    private static RunRecordDetail RunDetail(string? endState = "exited", string? importedAt = "2026-10-04T08:01:05.000Z", string? output = "out\n") =>
        new(4, Session, "2026-10-04T08:00:10.000Z", LogLevel.Info, 3, "/repo/dev.command", 4242, "2026-10-04T08:00:10.123Z",
            "/runs/3.log", endState is null ? null : "2026-10-04T08:01:00.000Z", endState, endState is null ? null : 0,
            importedAt, output is null ? null : Encoding.UTF8.GetBytes(output));

    [AvaloniaFact]
    public void Turning_on_exit_code_errors_replaces_a_detail_read_that_is_still_pending() =>
        PendingDetailFollowsCurrentExitCodePolicy(initialPolicy: false);

    [AvaloniaFact]
    public void Turning_off_exit_code_errors_replaces_a_detail_read_that_is_still_pending() =>
        PendingDetailFollowsCurrentExitCodePolicy(initialPolicy: true);

    private void PendingDetailFollowsCurrentExitCodePolicy(bool initialPolicy)
    {
        var originalPolicy = ExitCodes.NonZeroIsError;
        var stale = new TaskCompletionSource<RecordDetail?>();
        var current = new TaskCompletionSource<RecordDetail?>();
        var policies = new List<bool>();
        _reader.DetailWithPolicy = (_, _, policy) =>
        {
            policies.Add(policy);
            return policies.Count == 1 ? stale.Task : current.Task;
        };
        RecordsWindowViewModel? vm = null;
        try
        {
            ExitCodes.Use(initialPolicy);
            vm = Started();
            vm.SelectedRecord = vm.Rows[0];
            Pump();
            Assert.False(vm.HasDetail);

            ExitCodes.Use(!initialPolicy);
            Pump();
            Assert.Equal(new[] { initialPolicy, !initialPolicy }, policies);
            Assert.False(vm.HasDetail);

            stale.SetResult(RunDetail() with { Level = initialPolicy ? LogLevel.Error : LogLevel.Info, ExitCode = 7 });
            Pump();
            Assert.False(vm.HasDetail); // The old result must not settle the replacement selection.

            current.SetResult(RunDetail() with { Level = initialPolicy ? LogLevel.Info : LogLevel.Error, ExitCode = 7 });
            Pump();
            Assert.True(vm.HasDetail);
            Assert.Equal(!initialPolicy, vm.DetailIsError);
        }
        finally
        {
            vm?.Dispose();
            stale.TrySetResult(null);
            current.TrySetResult(null);
            ExitCodes.Use(originalPolicy);
            Pump();
        }
    }

    [AvaloniaFact]
    public void Shows_everything_a_selected_run_holds()
    {
        _reader.Detail = (_, _) => Task.FromResult<RecordDetail?>(new RunRecordDetail(
            4, Session, "2026-10-04T08:00:10.000Z", LogLevel.Error, 3, "/repo/dev.command",
            4242, "2026-10-04T08:00:10.123Z", "/runs/3.log", "2026-10-04T08:01:00.000Z", "exited", 3, "2026-10-04T08:01:05.000Z",
            Encoding.UTF8.GetBytes("\u001b[31mboom\u001b[0m\r\nprogress 1\rprogress 9\n")));
        var vm = Started();

        vm.SelectedRecord = vm.Rows[0];
        Pump();

        Assert.Equal((RecordKind.Run, 4L), Assert.Single(_reader.DetailReads));
        Assert.True(vm.HasDetail);
        Assert.Equal("/repo/dev.command", vm.DetailTitle);
        Assert.True(vm.DetailIsError);
        var fields = vm.DetailFields.ToDictionary(field => field.Label, field => field.Value);
        Assert.True(vm.DetailIsPathTitle);
        Assert.Equal("exited", fields[English.Of("records.endState")]);
        Assert.Equal("3", fields[English.Of("records.exitCode")]);
        Assert.Equal("4242", fields[English.Of("records.processId")]);
        Assert.Equal("/runs/3.log", fields[English.Of("records.outputFile")]);
        Assert.Contains(English.Of("records.started"), fields.Keys);
        Assert.Contains(English.Of("records.imported"), fields.Keys);
        Assert.Contains(English.Of("records.thisLaunch", ("time", "")).Trim(), fields[English.Of("records.launch")]);
        var output = Assert.Single(vm.DetailBlocks);
        Assert.Equal(English.Of("records.output"), output.Label);
        Assert.Equal("boom\nprogress 9", output.Text);
        Assert.Null(output.Note);
        Assert.False(vm.CanOpenFullOutput);
    }

    [AvaloniaFact]
    public void A_very_large_output_shows_its_first_megabyte_and_says_how_much_more_is_stored()
    {
        // 1 MB and a half in 1 KB lines: the pane shows the first megabyte and notes the 0.5 MB left out.
        var stored = string.Concat(Enumerable.Repeat(new string('x', 1023) + "\n", 1536));
        _reader.Detail = (_, _) => Task.FromResult<RecordDetail?>(RunDetail(output: stored));
        var vm = Started();
        vm.SelectedRecord = vm.Rows[0];
        Pump();

        var output = Assert.Single(vm.DetailBlocks);
        Assert.Equal(RecordFormat.OutputShownBytes - 1, output.Text.Length);
        Assert.Equal(English.Of("records.outputMore", ("more", 0.5)), output.Note);
        Assert.Contains(English.Of("records.openFullOutput"), output.Note);
        Assert.True(vm.CanOpenFullOutput);
    }

    [AvaloniaFact]
    public void A_shortened_long_line_notes_it_and_offers_the_full_output()
    {
        _reader.Detail = (_, _) => Task.FromResult<RecordDetail?>(RunDetail(output: new string('x', RecordFormat.OutputShownLineGraphemes + 1)));
        var vm = Started();
        vm.SelectedRecord = vm.Rows[0];
        Pump();

        Assert.Equal(English.Of("records.outputLinesShortened"), Assert.Single(vm.DetailBlocks).Note);
        Assert.True(vm.CanOpenFullOutput);
    }

    [AvaloniaFact]
    public async Task Open_full_output_hands_the_whole_stored_output_on_and_keeps_a_failure_until_dismissed()
    {
        var stored = Encoding.UTF8.GetBytes(new string('x', RecordFormat.OutputShownBytes * 2) + "far end");
        _reader.Detail = (_, _) => Task.FromResult<RecordDetail?>(RunDetail(output: new string('x', RecordFormat.OutputShownBytes + 1)));
        _reader.Output = _ => Task.FromResult<byte[]?>(stored);
        var opened = new System.Collections.Generic.List<(long Id, byte[] Output)>();
        var succeeds = true;
        var vm = new RecordsWindowViewModel(_reader, _stateStore, _stateStore.Value, _time, (run, output) =>
        {
            using var copy = new System.IO.MemoryStream();
            output.CopyTo(copy);
            opened.Add((run.Id, copy.ToArray()));
            return succeeds;
        });
        vm.Start();
        Pump();
        vm.SelectedRecord = vm.Rows[0];
        Pump();

        await vm.OpenFullOutputCommand.ExecuteAsync(null);
        Assert.Equal([4L], _reader.OutputReads);
        Assert.Equal(stored, Assert.Single(opened).Output); // streamed whole, not held as one value
        Assert.False(vm.OpenOutputFailed);

        succeeds = false;
        await vm.OpenFullOutputCommand.ExecuteAsync(null);
        Assert.True(vm.OpenOutputFailed);
        vm.DismissOpenOutputErrorCommand.Execute(null);
        Assert.False(vm.OpenOutputFailed);

        // A read that fails is the same failure, and choosing another record clears it with its run.
        _reader.Output = _ => Task.FromException<byte[]?>(new InvalidOperationException("busy (test)"));
        await vm.OpenFullOutputCommand.ExecuteAsync(null);
        Assert.True(vm.OpenOutputFailed);
        _reader.Detail = (_, _) => Task.FromResult<RecordDetail?>(null);
        vm.SelectedRecord = vm.Rows[1];
        Pump();
        Assert.False(vm.OpenOutputFailed);
        Assert.False(vm.CanOpenFullOutput);
        vm.Dispose();
    }

    [AvaloniaFact]
    public void A_run_with_no_output_yet_shows_its_fields_and_no_output_block()
    {
        _reader.Detail = (_, _) => Task.FromResult<RecordDetail?>(RunDetail(endState: null, importedAt: null, output: null));
        var vm = Started();

        vm.SelectedRecord = vm.Rows[0];
        Pump();

        Assert.True(vm.HasDetail);
        Assert.Empty(vm.DetailBlocks);
        var labels = vm.DetailFields.Select(field => field.Label).ToList();
        Assert.Contains(English.Of("records.started"), labels);
        Assert.DoesNotContain(English.Of("records.ended"), labels);
        Assert.DoesNotContain(English.Of("records.imported"), labels);
    }

    [AvaloniaFact]
    public void Hides_a_block_with_only_whitespace_or_empty_json_in_it()
    {
        _reader.Page = new RecordsPage([Failed, Scan], false);
        _reader.Detail = (kind, id) => Task.FromResult<RecordDetail?>(kind == RecordKind.Run
            ? RunDetail(output: " \r\n\t\n")
            : new ScanReportRecordDetail(id, Session, Scan.Time, null, "{}"));
        var vm = Started();

        vm.SelectedRecord = vm.Rows[0];
        Pump();
        Assert.True(vm.HasDetail);
        Assert.Empty(vm.DetailBlocks);

        vm.SelectedRecord = vm.Rows[1];
        Pump();
        Assert.True(vm.HasDetail);
        Assert.Empty(vm.DetailBlocks);
        Assert.NotEmpty(vm.DetailFields);
    }

    [AvaloniaFact]
    public void A_log_line_s_details_leave_out_what_the_pane_already_shows_and_hide_when_nothing_remains()
    {
        _reader.Page = new RecordsPage([Warning, Newer], false);
        _reader.Detail = (_, id) => Task.FromResult<RecordDetail?>(id == Warning.Id
            ? new LogRecordDetail(id, Session, Warning.Time, LogLevel.Warn, Warning.Title,
                """{"time":"2026-10-04T08:00:30.000Z","level":"warn","message":"run: terminate failed","id":3,"error":{"message":"denied"}}""")
            : new LogRecordDetail(id, Session, Newer.Time, LogLevel.Info, Newer.Title,
                """{"time":"2026-10-04T08:02:00.000Z","level":"info","message":"app.later"}"""));
        var vm = Started();

        vm.SelectedRecord = vm.Rows[0];
        Pump();
        var details = Assert.Single(vm.DetailBlocks);
        Assert.Equal(English.Of("records.details"), details.Label);
        Assert.Equal("{\n  \"id\": 3,\n  \"error\": {\n    \"message\": \"denied\"\n  }\n}", details.Text.Replace("\r\n", "\n"));

        vm.SelectedRecord = vm.Rows[1];
        Pump();
        Assert.True(vm.HasDetail);
        Assert.Empty(vm.DetailBlocks);
    }

    [AvaloniaFact]
    public void Reads_a_selected_run_again_as_records_arrive_until_it_has_its_end_and_output()
    {
        var shown = RunDetail(endState: null, importedAt: null, output: null);
        _reader.Detail = (_, _) => Task.FromResult<RecordDetail?>(shown);
        var vm = Started();
        vm.SelectedRecord = vm.Rows[0];
        Pump();
        var fields = vm.DetailFields.ToList();

        // Read again, but nothing new: the pane is left as it is.
        _reader.RaiseStored();
        Pump();
        Advance(RecordsWindowViewModel.LiveInterval);
        Assert.Equal(2, _reader.DetailReads.Count);
        Assert.Same(fields[0], vm.DetailFields[0]);

        // Its end arrives, then its output.
        _reader.Detail = (_, _) => Task.FromResult<RecordDetail?>(RunDetail(importedAt: null, output: null));
        _reader.RaiseStored();
        Pump();
        Advance(RecordsWindowViewModel.LiveInterval);
        Assert.Equal("exited", vm.DetailFields.Single(field => field.Label == English.Of("records.endState")).Value);
        Assert.Empty(vm.DetailBlocks);

        _reader.Detail = (_, _) => Task.FromResult<RecordDetail?>(RunDetail());
        _reader.RaiseStored();
        Pump();
        Advance(RecordsWindowViewModel.LiveInterval);
        Assert.Equal("out", Assert.Single(vm.DetailBlocks).Text);

        // Settled: new records no longer read it again.
        var reads = _reader.DetailReads.Count;
        _reader.RaiseStored();
        Pump();
        Advance(RecordsWindowViewModel.LiveInterval);
        Assert.Equal(reads, _reader.DetailReads.Count);
    }

    [AvaloniaFact]
    public void Shows_a_log_line_and_a_scan_report_as_stored_json_indented()
    {
        _reader.Page = new RecordsPage([Warning, Scan], false);
        _reader.Detail = (kind, id) => Task.FromResult<RecordDetail?>(kind == RecordKind.Log
            ? new LogRecordDetail(id, Session, Warning.Time, LogLevel.Warn, "run: terminate failed", """{"level":"warn","id":3,"script":"/a"}""")
            : new ScanReportRecordDetail(id, Session, Scan.Time, 3, """{"roots":["/code"]}"""));
        var vm = Started();

        vm.SelectedRecord = vm.Rows[0];
        Pump();
        Assert.Equal("run: terminate failed", vm.DetailTitle);
        Assert.Equal("{\n  \"id\": 3,\n  \"script\": \"/a\"\n}", Assert.Single(vm.DetailBlocks).Text.Replace("\r\n", "\n"));
        // The header shows the message on one line, so the detail holds it whole.
        Assert.Equal("run: terminate failed", vm.DetailFields.Single(f => f.Label == English.Of("records.message")).Value);

        vm.SelectedRecord = vm.Rows[1];
        Pump();
        Assert.Equal(English.Of("records.scanFound", ("count", 3)), vm.DetailTitle);
        Assert.Equal(English.Of("records.scanFound", ("count", 3)), vm.Rows[1].Title);
        Assert.Equal("/code", vm.Rows[1].FirstRoot);
        Assert.Equal(English.Of("list.andMore", ("count", 1)), vm.Rows[1].MoreRootsText);
        Assert.Equal(English.Of("records.report"), Assert.Single(vm.DetailBlocks).Label);
    }

    [AvaloniaFact]
    public void A_record_that_cannot_be_read_says_so()
    {
        _reader.Detail = (_, _) => Task.FromException<RecordDetail?>(new InvalidOperationException("gone (test)"));
        var vm = Started();

        vm.SelectedRecord = vm.Rows[1];
        Pump();

        Assert.True(vm.ShowDetailFailed);
        Assert.False(vm.HasDetail);
    }

    [AvaloniaFact]
    public void Reads_the_next_page_from_the_last_row_once_the_list_nears_its_end()
    {
        _reader.Next(new RecordsPage([Failed], true));
        _reader.Next(new RecordsPage([Warning], false));
        var vm = Started();
        Assert.Single(_reader.PageQueries);

        vm.ListScrolled(atTop: false, nearEnd: true);
        Pump();

        Assert.Equal(2, _reader.PageQueries.Count);
        Assert.Equal(CursorOf(Failed), _reader.LastQuery.After);
        Assert.Equal(["/repo/dev.command", "run: terminate failed"], Titles(vm));

        // Nothing more to read: the end reads nothing.
        vm.ListScrolled(atTop: false, nearEnd: true);
        Pump();
        Assert.Equal(2, _reader.PageQueries.Count);
    }

    [AvaloniaFact]
    public void Selecting_the_last_row_reads_the_next_page()
    {
        _reader.Next(new RecordsPage([Failed, Warning], true));
        _reader.Next(new RecordsPage([Scan], false));
        var vm = Started();

        vm.SelectedRecord = vm.Rows[1];
        Pump();

        Assert.Equal(CursorOf(Warning), _reader.LastQuery.After);
        Assert.Equal(3, vm.Rows.Count);
        Assert.Same(vm.Rows[1], vm.SelectedRecord);
    }

    [AvaloniaFact]
    public void Makes_one_request_while_a_page_is_being_read()
    {
        _reader.Next(new RecordsPage([Failed, Warning], true));
        _reader.Next(new TaskCompletionSource<RecordsPage>().Task);
        var vm = Started();

        vm.ListScrolled(atTop: false, nearEnd: true);
        vm.ListScrolled(atTop: false, nearEnd: true);
        vm.LoadMore();
        Pump();

        Assert.Equal(2, _reader.PageQueries.Count);
        Assert.True(vm.LoadingMore);
        Assert.Equal(2, vm.Rows.Count);
    }

    [AvaloniaFact]
    public void Reads_the_next_page_by_itself_while_a_page_leaves_the_list_short()
    {
        _reader.Next(new RecordsPage([Failed], true));
        _reader.Next(new RecordsPage([Warning], false));
        var vm = Started();

        vm.ListLaidOut(atTop: true, nearEnd: true);
        Pump();

        Assert.Equal(["/repo/dev.command", "run: terminate failed"], Titles(vm));
    }

    [AvaloniaFact]
    public void Keeps_a_failed_page_noted_and_reads_it_again_when_the_end_is_reached_again()
    {
        _reader.Next(new RecordsPage([Failed], true));
        _reader.NextFails();
        _reader.Next(new RecordsPage([Warning], false));
        var vm = Started();

        vm.ListScrolled(atTop: false, nearEnd: true);
        Pump();
        Assert.True(vm.MoreFailed);
        Assert.Single(vm.Rows);

        // Laying the list out again does not retry; the reader reaching the end does.
        vm.ListLaidOut(atTop: false, nearEnd: true);
        Pump();
        Assert.Equal(2, _reader.PageQueries.Count);

        vm.ListScrolled(atTop: false, nearEnd: true);
        Pump();
        Assert.Equal(3, _reader.PageQueries.Count);
        Assert.Equal(CursorOf(Failed), _reader.LastQuery.After);
        Assert.False(vm.MoreFailed);
        Assert.Equal(["/repo/dev.command", "run: terminate failed"], Titles(vm));
    }

    [AvaloniaFact]
    public void Drops_a_page_read_for_filters_that_were_changed_meanwhile()
    {
        var stale = new TaskCompletionSource<RecordsPage>();
        _reader.Next(stale.Task);
        _reader.Next(new RecordsPage([Warning], false));
        var vm = Started();

        vm.SelectedLevel = vm.LevelOptions.Single(option => Equals(option.Value, RecordLevelFilter.Warn));
        Pump();
        stale.SetResult(new RecordsPage([Failed, Warning], false));
        Pump();

        Assert.Equal(["run: terminate failed"], Titles(vm));
    }

    [AvaloniaFact]
    public void Re_reads_the_newest_page_once_for_a_burst_of_new_records_keeping_the_rows_shown()
    {
        var vm = Started();
        var next = new TaskCompletionSource<RecordsPage>();
        _reader.Next(next.Task);

        _reader.RaiseStored();
        _reader.RaiseStored();
        _reader.RaiseStored();
        Pump();
        Advance(RecordsWindowViewModel.LiveInterval);

        Assert.Equal(2, _reader.PageQueries.Count);
        Assert.Equal(RecordsQuery.All with { Through = CursorOf(Warning) }, _reader.LastQuery);
        Assert.Equal(1, _reader.SourceReads); // Sessions are offered once per window, not on every live page refresh.
        Assert.Equal(2, vm.Rows.Count);
        Assert.False(vm.ShowLoadingNote);

        next.SetResult(new RecordsPage([Newer, Failed, Warning], false));
        Pump();
        Assert.Equal(["app.later", "/repo/dev.command", "run: terminate failed"], Titles(vm));
    }

    [AvaloniaFact]
    public void LiveRefreshRemovesRowsThatNoLongerMatchAndKeepsTheLoadedFloor()
    {
        var vm = Started();
        _reader.Next(new RecordsPage([], true));
        _reader.RaiseStored();
        Pump();
        Advance(RecordsWindowViewModel.LiveInterval);
        Assert.Empty(vm.Rows);
        _reader.Next(new RecordsPage([Newer], true));
        _reader.RaiseStored();
        Pump();
        Advance(RecordsWindowViewModel.LiveInterval);
        Assert.Equal(CursorOf(Warning), _reader.LastQuery.Through);
        Assert.Equal(["app.later"], Titles(vm));
    }

    [AvaloniaFact]
    public void LiveRefreshCannotDiscardAMorePageAdmittedWhileItWasReading()
    {
        _reader.Next(new RecordsPage([Failed, Warning], true));
        var vm = Started();
        var live = new TaskCompletionSource<RecordsPage>();
        _reader.Next(live.Task);
        var more = new TaskCompletionSource<RecordsPage>();
        _reader.Next(more.Task);
        _reader.RaiseStored();
        Pump();
        Advance(RecordsWindowViewModel.LiveInterval);
        vm.LoadMore();
        Pump();
        Assert.Equal(2, _reader.PageQueries.Count);
        live.SetResult(new RecordsPage([Newer, Failed, Warning], true));
        Pump();
        Assert.Equal(CursorOf(Warning), _reader.LastQuery.After);
        Assert.True(vm.LoadingMore);
        more.SetResult(new RecordsPage([Scan], false));
        Pump();
        Assert.False(vm.LoadingMore);
        Assert.Equal(["app.later", "/repo/dev.command", "run: terminate failed", ""], Titles(vm));
    }

    [AvaloniaFact]
    public void ScrollingAwayDuringALiveReadDefersItsReplacementUntilReturning()
    {
        var vm = Started();
        var live = new TaskCompletionSource<RecordsPage>();
        _reader.Next(live.Task);
        _reader.RaiseStored();
        Pump();
        Advance(RecordsWindowViewModel.LiveInterval);
        vm.ListScrolled(atTop: false, nearEnd: false);
        live.SetResult(new RecordsPage([Newer], false));
        Pump();
        Assert.Equal(2, vm.Rows.Count);
        _reader.Next(new RecordsPage([Newer, Failed, Warning], false));
        vm.ListScrolled(atTop: true, nearEnd: false);
        Pump();
        Assert.Equal(3, vm.Rows.Count);
    }

    [AvaloniaFact]
    public void LiveSignalsDuringMoreCoalesceAndRefreshTheExpandedRange()
    {
        _reader.Next(new RecordsPage([Failed, Warning], true));
        var vm = Started();
        var more = new TaskCompletionSource<RecordsPage>();
        _reader.Next(more.Task);
        vm.LoadMore();
        _reader.RaiseStored();
        Pump();
        Advance(RecordsWindowViewModel.LiveInterval);
        _reader.RaiseStored();
        Pump();
        Advance(RecordsWindowViewModel.LiveInterval);
        Assert.Equal(2, _reader.PageQueries.Count);
        _reader.Next(new RecordsPage([Newer, Failed, Warning, Scan], false));
        more.SetResult(new RecordsPage([Scan], false));
        Pump();
        Assert.Equal(3, _reader.PageQueries.Count);
        Assert.Equal(CursorOf(Scan), _reader.LastQuery.Through);
        Assert.Equal(4, vm.Rows.Count);
    }

    [AvaloniaFact]
    public void InitialReadSettlesBeforeAnOverlappingLiveSignalRefreshesIt()
    {
        var initial = new TaskCompletionSource<RecordsPage>();
        _reader.Next(initial.Task);
        var vm = Started();
        _reader.RaiseStored();
        Pump();
        Advance(RecordsWindowViewModel.LiveInterval);
        Assert.Single(_reader.PageQueries);
        _reader.Next(new RecordsPage([Newer, Failed, Warning], false));
        initial.SetResult(new RecordsPage([Failed, Warning], false));
        Pump();
        Assert.Equal(3, vm.Rows.Count);
        Assert.Equal(CursorOf(Warning), _reader.LastQuery.Through);
    }

    [AvaloniaFact]
    public void Leaves_the_list_alone_while_scrolled_down_and_shows_new_records_once_back_at_the_top()
    {
        var vm = Started();
        vm.ListScrolled(atTop: false, nearEnd: false);
        _reader.Next(new RecordsPage([Newer, Failed, Warning], false));

        _reader.RaiseStored();
        Pump();
        Advance(RecordsWindowViewModel.LiveInterval);
        Assert.Single(_reader.PageQueries);
        Assert.Equal(2, vm.Rows.Count);

        vm.ListScrolled(atTop: true, nearEnd: false);
        Pump();
        Assert.Equal(2, _reader.PageQueries.Count);
        Assert.Equal(["app.later", "/repo/dev.command", "run: terminate failed"], Titles(vm));
    }

    [AvaloniaFact]
    public void Keeps_the_selected_record_selected_through_an_update()
    {
        var vm = Started();
        vm.SelectedRecord = vm.Rows[1];
        Pump();
        var selected = vm.SelectedRecord;
        _reader.Next(new RecordsPage([Newer, Failed, Warning], false));

        _reader.RaiseStored();
        Pump();
        Advance(RecordsWindowViewModel.LiveInterval);

        Assert.Equal(3, vm.Rows.Count);
        Assert.Same(selected, vm.SelectedRecord);
        Assert.Same(vm.Rows[2], vm.SelectedRecord);
        Assert.Single(_reader.DetailReads);
    }

    [AvaloniaFact]
    public void Ignores_new_record_signals_after_a_failed_read_until_a_read_succeeds()
    {
        var vm = Started();
        _reader.NextFails();

        _reader.RaiseStored();
        Pump();
        Advance(RecordsWindowViewModel.LiveInterval);
        Assert.Equal(2, _reader.PageQueries.Count);

        // The failure is logged, and a stored log line signals again: no read follows.
        _reader.RaiseStored();
        Pump();
        Advance(RecordsWindowViewModel.LiveInterval);
        Assert.Equal(2, _reader.PageQueries.Count);
        Assert.Equal(2, vm.Rows.Count);

        // A read that succeeds lets new records through again.
        vm.SelectedKind = vm.KindOptions[1];
        Pump();
        _reader.RaiseStored();
        Pump();
        Advance(RecordsWindowViewModel.LiveInterval);
        Assert.Equal(4, _reader.PageQueries.Count);
    }

    [AvaloniaFact]
    public void Stops_listening_for_new_records_when_it_closes()
    {
        var vm = Started();
        Assert.True(_reader.IsListening);

        vm.Dispose();

        Assert.False(_reader.IsListening);
    }

    [AvaloniaFact]
    public async Task Keeps_the_list_width_a_drag_ended_at_healed_into_its_bounds()
    {
        _stateStore.Value.RecordsListWidth = 9999;
        var vm = new RecordsWindowViewModel(_reader, _stateStore, _stateStore.Value, _time);
        Assert.Equal(RecordsLayout.ListMaxWidth, vm.ListWidth);

        await vm.CommitListWidthAsync(451.6);

        Assert.Equal(1, _stateStore.SaveCount);
        Assert.Equal(452, _stateStore.Value.RecordsListWidth);
        Assert.Equal(452, vm.ListWidth);
    }

    [AvaloniaFact]
    public async Task Keeps_its_placement_beside_the_main_window_s()
    {
        _stateStore.Value.WindowWidth = 1100;
        var vm = new RecordsWindowViewModel(_reader, _stateStore, _stateStore.Value, _time);

        await vm.SavePlacementAsync(-1400, 80, 1000.5, 680, maximized: true);

        var saved = _stateStore.LastSaved!;
        Assert.Equal(-1400, saved.RecordsWindowPositionX);
        Assert.Equal(80, saved.RecordsWindowPositionY);
        Assert.Equal(1000.5, saved.RecordsWindowWidth);
        Assert.Equal(680, saved.RecordsWindowHeight);
        Assert.True(saved.RecordsWindowMaximized);
        Assert.Equal(1100, saved.WindowWidth);
    }
}
