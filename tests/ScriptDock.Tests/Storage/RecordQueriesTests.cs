using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ScriptDock;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.Storage;
using ScriptDock.ViewModels;
using ScriptDock.Tests.Fakes;
using Xunit;

namespace ScriptDock.Tests.Storage;

/// <summary>
/// The Records window's reads against a real <c>records.sqlite3</c>: newest first across the kinds,
/// keyset pages, each filter, each record whole, the launches, and the stored signal that only writes raise.
/// </summary>
public sealed class RecordQueriesTests : IDisposable
{
    private static readonly DateTimeOffset SessionStart = new(2026, 10, 2, 3, 0, 0, TimeSpan.Zero);
    private const string Session = "2026-10-02T03:00:00.000Z";

    // The reads as they are with an exit code other than 0 counted as an error; the default is off.
    private static readonly RecordsQuery NonZeroIsError = RecordsQuery.All with { NonZeroExitIsError = true };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scriptdock-record-queries-tests", NanoId.New());

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private static string At(int minute, int second = 0) =>
        TimestampConventions.IsoMillis(SessionStart.AddMinutes(minute).AddSeconds(second));

    private static void Log(RecordStore records, string time, string level, string message, string? extra = null) =>
        records.AddLog(time, level, message, $"{{\"time\":\"{time}\",\"level\":\"{level}\",\"message\":\"{message}\"{extra}}}");

    private static RunRecord Run(int run, string script) =>
        new(Session, run, SessionStart.AddMinutes(run), script, 4000 + run, SessionStart.AddMinutes(run).AddMilliseconds(5), $"/runs/{run}.log");

    private static ScanReport Report(int minute, params string[] found) => new()
    {
        CompletedAt = SessionStart.AddMinutes(minute),
        Roots = ["/code", "/work"],
        Found = found,
        PrunedDirectories = [new IgnoredEntry("/code/node_modules", "/node_modules/")],
        SkippedFiles = [],
        Inaccessible = [],
        InvalidPatterns = [],
    };

    // A log line, a warning, a failed run, a run that exited cleanly, each with its output, and a scan.
    private async Task<RecordStore> Seeded(Action<RecordStore>? opened = null)
    {
        var records = new RecordStore(_dir, SessionStart);
        try
        {
            opened?.Invoke(records);
            Log(records, At(1), "info", "startup");
            Log(records, At(2), "warn", "run: terminate failed", ",\"id\":3");
            records.AddScanReport(Report(3, "/code/a/dev.command", "/code/b/build.command"));
            await records.AddRunAsync(Run(4, "/code/a/dev.command"));
            await records.AddRunEndAsync(new RunEnd(Session, 4, SessionStart.AddMinutes(5), "exited", 3));
            await records.AddRunOutputAsync(Run(4, "/code/a/dev.command"), Encoding.UTF8.GetBytes("listening on 100% of ports\n"));
            await records.AddRunAsync(Run(6, "/code/b/build.command"));
            await records.AddRunEndAsync(new RunEnd(Session, 6, SessionStart.AddMinutes(7), "exited", 0));
            await records.AddRunOutputAsync(Run(6, "/code/b/build.command"), Encoding.UTF8.GetBytes("built\n"));
            return records;
        }
        catch
        {
            records.Dispose();
            throw;
        }
    }

    private static Task<RecordsPage> Page(RecordStore records, RecordsQuery query) => records.ReadRecordsPageAsync(query, cancellation: TestContext.Current.CancellationToken);

    [Fact]
    public async Task ResidentRefreshIsBoundedAndReversePagesReachTheNearestNewerRecords()
    {
        using var records = new RecordStore(_dir, SessionStart);
        for (var index = 1; index <= 700; index++) await records.AddRunAsync(Run(index, $"/{index}.command"));
        var refreshed = await Page(records, RecordsQuery.All with { Through = new RecordCursor(At(1), RecordKind.Run, 1) });
        Assert.Equal(RecordsQuery.ResidentLimit, refreshed.Records.Count);
        Assert.True(refreshed.More);
        var previous = await Page(records, RecordsQuery.All with { Before = new RecordCursor(At(300), RecordKind.Run, 300) });
        Assert.Equal(400, previous.Records[0].Id);
        Assert.Equal(301, previous.Records[^1].Id);
        Assert.True(previous.More);
        var bounded = await Page(records, RecordsQuery.All with
        {
            From = new RecordCursor(At(400), RecordKind.Run, 400),
            Through = new RecordCursor(At(201), RecordKind.Run, 201),
        });
        Assert.Equal(200, bounded.Records.Count);
        Assert.Equal(400, bounded.Records[0].Id);
        Assert.Equal(201, bounded.Records[^1].Id);
    }

    [Fact]
    public async Task LargeScanReportHasABoundedPreviewAndACompleteIndependentExport()
    {
        using var records = new RecordStore(_dir, SessionStart);
        var paths = Enumerable.Range(0, 20000).Select(i => $"/code/{i}/script.command").ToArray();
        records.AddScanReport(Report(3, paths));
        var row = Assert.Single((await Page(records, RecordsQuery.All)).Records);
        var detail = Assert.IsType<ScanReportRecordDetail>(await records.ReadRecordDetailAsync(row.Kind, row.Id, false, TestContext.Current.CancellationToken));
        Assert.True(detail.Partial);
        Assert.True(Encoding.UTF8.GetByteCount(detail.Report) <= ScanReportRecordDetail.PreviewBytes);
        using var complete = new MemoryStream();
        Assert.True(await records.ReadScanReportAsync(row.Id, stream => stream.CopyTo(complete)));
        using var document = System.Text.Json.JsonDocument.Parse(complete.ToArray());
        Assert.Equal(paths.Length, document.RootElement.GetProperty("found").GetArrayLength());
        Assert.Equal(paths[^1], document.RootElement.GetProperty("found")[paths.Length - 1].GetString());
        Assert.False(await records.ReadScanReportAsync(long.MaxValue, _ => Assert.Fail("Missing report must not export")));
    }

    [Fact]
    public async Task FailedSeedingClosesTheRecordsOwnerBeforeReturningTheFailure()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(Path.Combine(_dir, "records.sqlite3"));
        RecordStore? opened = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Seeded(records => opened = records));
        Assert.True(opened!.DatabaseUnavailable);
        var owner = (Thread)typeof(RecordStore).GetField("_owner", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(opened)!;
        try { Assert.False(owner.IsAlive); }
        finally { opened.Dispose(); }
    }

    [Fact]
    public async Task DetailReadsOnlyThePreviewWhileSearchStillReadsTheWholeStoredOutput()
    {
        using var records = new RecordStore(_dir, SessionStart);
        var run = Run(1, "/large.command");
        await records.AddRunAsync(run);
        // Lines of 1 KB up to one byte before the preview limit, where a four-byte emoji straddles it.
        var lines = string.Concat(Enumerable.Repeat(new string('a', 1023) + "\n", 1023));
        var prefix = lines + new string('a', RunRecordDetail.PreviewBytes - 1 - Encoding.UTF8.GetByteCount(lines));
        var bytes = Encoding.UTF8.GetBytes(prefix + "😀" + new string('b', 10000) + "far marker");
        await records.AddRunOutputAsync(run, bytes);
        var row = Assert.Single((await Page(records, RecordsQuery.All with { Search = "far marker" })).Records);
        var detail = Assert.IsType<RunRecordDetail>(await records.ReadRecordDetailAsync(row.Kind, row.Id, nonZeroExitIsError: false, cancellation: TestContext.Current.CancellationToken));
        Assert.True(detail.Output!.Length <= RunRecordDetail.PreviewBytes + 1);
        Assert.Equal(bytes.LongLength, detail.OutputLength);
        var preview = Assert.IsType<OutputPreview>(RecordFormat.Output(detail.Output, totalBytes: detail.OutputLength));
        Assert.Equal(prefix, preview.Text);
        Assert.Equal(bytes.LongLength - (RunRecordDetail.PreviewBytes - 1), preview.MoreBytes);
        Assert.Equal(bytes, await records.ReadRunOutputAsync(row.Id));
    }

    [Fact]
    public async Task LiveRangeIsInclusiveAndReconcilesChangesBeyondTheNewestPage()
    {
        using var records = new RecordStore(_dir, SessionStart);
        for (var index = 1; index <= 120; index++) await records.AddRunAsync(Run(index, $"/{index}.command"));
        var floor = new RecordCursor(At(10), RecordKind.Run, 10);
        var query = NonZeroIsError with { Level = RecordLevelFilter.Info, Through = floor };
        var before = await Page(records, query);
        Assert.Equal(111, before.Records.Count);
        Assert.True(before.More);
        Assert.Equal(10, before.Records[^1].Id);
        await records.AddRunEndAsync(new RunEnd(Session, 10, SessionStart.AddHours(3), "exited", 1));
        await records.AddRunEndAsync(new RunEnd(Session, 11, SessionStart.AddHours(3), "exited", 2));
        var after = await Page(records, query);
        Assert.Equal(109, after.Records.Count);
        Assert.DoesNotContain(after.Records, row => row.Id is 10 or 11);
        Assert.True(after.More);
        var all = await Page(records, query with { Level = null });
        Assert.Equal(LogLevel.Error, all.Records.Single(row => row.Id == 11).Level);
    }

    [Fact]
    public async Task LiveRangeHandlesTiesEmptyMembershipAndOutputEnteringTheSearch()
    {
        using var records = new RecordStore(_dir, SessionStart);
        Log(records, At(1), "info", "older");
        Log(records, At(2), "info", "needle first");
        Log(records, At(2), "info", "needle second");
        var run = Run(2, "/serve.command");
        await records.AddRunAsync(run);
        var page = await Page(records, RecordsQuery.All);
        var floorRow = page.Records.Single(row => row.Title == "needle second");
        var floor = new RecordCursor(floorRow.Time, floorRow.Kind, floorRow.Id);
        var query = RecordsQuery.All with { Through = floor };
        var range = await Page(records, query);
        Assert.Equal(["/serve.command", "needle second"], range.Records.Select(row => row.Title));
        Assert.True(range.More);
        var excluded = await Page(records, query with { Search = "older" });
        Assert.Empty(excluded.Records);
        Assert.True(excluded.More);
        var search = query with { Search = "needle" };
        Assert.Equal("needle second", Assert.Single((await Page(records, search)).Records).Title);
        await records.AddRunOutputAsync(run, Encoding.UTF8.GetBytes("needle in newly imported output"));
        Assert.Equal(["/serve.command", "needle second"], (await Page(records, search)).Records.Select(row => row.Title));
        Assert.True((await Page(records, search)).More);
    }

    [Fact]
    public async Task Lists_every_kind_newest_first_with_its_summary()
    {
        using var records = await Seeded();

        var page = await Page(records, NonZeroIsError);

        // A run is listed at its start, whenever its output was imported.
        Assert.False(page.More);
        Assert.Equal(
            [RecordKind.Run, RecordKind.Run, RecordKind.ScanReport, RecordKind.Log, RecordKind.Log],
            page.Records.Select(record => record.Kind));
        Assert.Equal([At(6), At(4)], page.Records.Take(2).Select(record => record.Time));
        var scan = page.Records[2];
        Assert.Equal(LogLevel.Info, scan.Level);
        Assert.Equal(2, scan.Found);
        Assert.Equal("""["/code","/work"]""", scan.Text);
        Assert.Equal(At(3), scan.Time);
        var outputs = page.Records.Take(2).ToDictionary(record => record.Title, record => record.Level);
        Assert.Equal(LogLevel.Error, outputs["/code/a/dev.command"]);
        Assert.Equal(LogLevel.Info, outputs["/code/b/build.command"]);
        Assert.Equal(("run: terminate failed", LogLevel.Warn), (page.Records[3].Title, page.Records[3].Level));
        Assert.All(page.Records, record => Assert.Equal(Session, record.Session));
    }

    [Fact]
    public async Task Pages_a_hundred_at_a_time_from_the_last_record_shown()
    {
        using var records = new RecordStore(_dir, SessionStart);
        for (var index = 0; index < 150; index++)
            Log(records, At(0, index), "info", $"line {index}");
        // Two lines at one instant are told apart by id.
        Log(records, At(0, 149), "info", "line 149 again");

        var first = await Page(records, RecordsQuery.All);
        var last = first.Records[^1];
        var second = await Page(records, RecordsQuery.All with { After = new RecordCursor(last.Time, last.Kind, last.Id) });

        Assert.Equal(RecordQueries.PageSize, first.Records.Count);
        Assert.True(first.More);
        Assert.Equal(["line 149 again", "line 149"], first.Records.Take(2).Select(record => record.Title));
        Assert.Equal(51, second.Records.Count);
        Assert.False(second.More);
        Assert.Empty(first.Records.Select(record => record.Key).Intersect(second.Records.Select(record => record.Key)));
        Assert.Equal("line 0", second.Records[^1].Title);
    }

    [Fact]
    public async Task Filters_by_launch_and_kind()
    {
        using (var earlier = new RecordStore(_dir, SessionStart.AddDays(-1)))
            Log(earlier, At(-60), "info", "yesterday");
        using var records = await Seeded();

        var launch = await Page(records, RecordsQuery.All with { Session = "2026-10-01T03:00:00.000Z" });
        var outputs = await Page(records, RecordsQuery.All with { Kind = RecordKind.Run });
        var scans = await Page(records, RecordsQuery.All with { Kind = RecordKind.ScanReport });

        Assert.Equal("yesterday", Assert.Single(launch.Records).Title);
        Assert.Equal(2, outputs.Records.Count);
        Assert.All(outputs.Records, record => Assert.Equal(RecordKind.Run, record.Kind));
        Assert.Equal(RecordKind.ScanReport, Assert.Single(scans.Records).Kind);
    }

    [Fact]
    public async Task Needs_attention_is_every_warning_and_error_and_a_failed_run_counts_as_an_error()
    {
        using var records = await Seeded();
        Log(records, At(8), "error", "scan failed");
        Log(records, At(9), "debug", "poll");

        var attention = await Page(records, NonZeroIsError with { Level = RecordLevelFilter.Attention });
        var errors = await Page(records, NonZeroIsError with { Level = RecordLevelFilter.Error });
        var info = await Page(records, NonZeroIsError with { Level = RecordLevelFilter.Info });
        var debug = await Page(records, NonZeroIsError with { Level = RecordLevelFilter.Debug });

        Assert.Equal(["scan failed", "/code/a/dev.command", "run: terminate failed"], attention.Records.Select(record => record.Title));
        Assert.Equal(["scan failed", "/code/a/dev.command"], errors.Records.Select(record => record.Title));
        Assert.Equal(
            [RecordKind.Run, RecordKind.ScanReport, RecordKind.Log],
            info.Records.Select(record => record.Kind));
        Assert.Equal("poll", Assert.Single(debug.Records).Title);
    }

    // The level is read under the setting, never stored, so the same runs read either way, and the exit code
    // stays in the detail. A run that could not start is an error whatever the setting.
    [Fact]
    public async Task A_non_zero_exit_is_an_error_only_under_the_setting()
    {
        using var records = await Seeded();
        await records.AddRunAsync(new RunRecord(Session, 9, SessionStart.AddMinutes(9), "/code/d/broken.command", null, null, "/runs/9.log"));
        await records.AddRunEndAsync(new RunEnd(Session, 9, SessionStart.AddMinutes(9), "failed", null));

        var off = await Page(records, RecordsQuery.All with { Kind = RecordKind.Run });
        var offAttention = await Page(records, RecordsQuery.All with { Level = RecordLevelFilter.Attention });
        var on = await Page(records, NonZeroIsError with { Kind = RecordKind.Run });
        var onAttention = await Page(records, NonZeroIsError with { Level = RecordLevelFilter.Attention });

        Assert.Equal(
            [("/code/d/broken.command", LogLevel.Error), ("/code/b/build.command", LogLevel.Info), ("/code/a/dev.command", LogLevel.Info)],
            off.Records.Select(record => (record.Title, record.Level)));
        Assert.Equal(["/code/d/broken.command", "run: terminate failed"], offAttention.Records.Select(record => record.Title));
        Assert.Equal(
            [("/code/d/broken.command", LogLevel.Error), ("/code/b/build.command", LogLevel.Info), ("/code/a/dev.command", LogLevel.Error)],
            on.Records.Select(record => (record.Title, record.Level)));
        Assert.Equal(["/code/d/broken.command", "/code/a/dev.command", "run: terminate failed"], onAttention.Records.Select(record => record.Title));

        var id = off.Records.Single(record => record.Title == "/code/a/dev.command").Id;
        var plain = Assert.IsType<RunRecordDetail>(await records.ReadRecordDetailAsync(RecordKind.Run, id, nonZeroExitIsError: false, cancellation: TestContext.Current.CancellationToken));
        var flagged = Assert.IsType<RunRecordDetail>(await records.ReadRecordDetailAsync(RecordKind.Run, id, nonZeroExitIsError: true, cancellation: TestContext.Current.CancellationToken));
        Assert.Equal((LogLevel.Info, "exited", 3), (plain.Level, plain.EndState, plain.ExitCode));
        Assert.Equal((LogLevel.Error, "exited", 3), (flagged.Level, flagged.EndState, flagged.ExitCode));
    }

    [Fact]
    public async Task Search_reaches_every_stored_field_and_takes_wildcards_literally()
    {
        using var records = await Seeded();

        var field = await Page(records, RecordsQuery.All with { Search = "\"id\":3" });
        var output = await Page(records, RecordsQuery.All with { Search = "100% of" });
        var wildcard = await Page(records, RecordsQuery.All with { Search = "100_" });
        var report = await Page(records, RecordsQuery.All with { Search = "node_modules" });
        var script = await Page(records, RecordsQuery.All with { Search = "BUILD.command", Kind = RecordKind.Run });

        Assert.Equal("run: terminate failed", Assert.Single(field.Records).Title);
        Assert.Equal("/code/a/dev.command", Assert.Single(output.Records).Title);
        Assert.Empty(wildcard.Records);
        Assert.Equal(RecordKind.ScanReport, Assert.Single(report.Records).Kind);
        Assert.Equal("/code/b/build.command", Assert.Single(script.Records).Title);
    }

    [Fact]
    public async Task Reads_each_kind_of_record_whole()
    {
        using var records = await Seeded();
        var page = await Page(records, NonZeroIsError);
        RecordSummary Of(RecordKind kind, string title) =>
            page.Records.First(record => record.Kind == kind && (title == "" || record.Title == title));

        var log = Assert.IsType<LogRecordDetail>(await records.ReadRecordDetailAsync(RecordKind.Log, Of(RecordKind.Log, "run: terminate failed").Id, nonZeroExitIsError: false, cancellation: TestContext.Current.CancellationToken));
        var run = Assert.IsType<RunRecordDetail>(await records.ReadRecordDetailAsync(RecordKind.Run, Of(RecordKind.Run, "/code/a/dev.command").Id, nonZeroExitIsError: true, cancellation: TestContext.Current.CancellationToken));
        var scan = Assert.IsType<ScanReportRecordDetail>(await records.ReadRecordDetailAsync(RecordKind.ScanReport, Of(RecordKind.ScanReport, "").Id, nonZeroExitIsError: false, cancellation: TestContext.Current.CancellationToken));

        Assert.Equal((At(2), LogLevel.Warn, "run: terminate failed"), (log.Time, log.Level, log.Message));
        Assert.Contains("\"id\":3", log.Line);

        Assert.Equal(LogLevel.Error, run.Level);
        Assert.Equal((4, "/code/a/dev.command", At(4), 4004), (run.Run, run.Script, run.Time, run.Pid));
        Assert.Equal(TimestampConventions.IsoMillis(SessionStart.AddMinutes(4).AddMilliseconds(5)), run.OsStartedAt);
        Assert.Equal(("/runs/4.log", At(5), "exited", 3), (run.OutputPath, run.EndedAt, run.EndState, run.ExitCode));
        Assert.NotNull(run.ImportedAt);
        Assert.Equal("listening on 100% of ports\n", Encoding.UTF8.GetString(run.Output!));

        Assert.Equal((At(3), 2), (scan.Time, scan.Found));
        Assert.Contains("\"prunedDirectories\"", scan.Report);

        Assert.Null(await records.ReadRecordDetailAsync(RecordKind.Log, 9999, nonZeroExitIsError: false, cancellation: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Lists_a_run_whose_output_was_never_imported_with_its_end_when_there_is_one()
    {
        using var records = await Seeded();
        // A run still going, and one that could not start and so wrote no output.
        await records.AddRunAsync(Run(8, "/code/c/serve.command"));
        await records.AddRunAsync(new RunRecord(Session, 9, SessionStart.AddMinutes(9), "/code/d/broken.command", null, null, "/runs/9.log"));
        await records.AddRunEndAsync(new RunEnd(Session, 9, SessionStart.AddMinutes(9), "failed", null));

        var page = await Page(records, NonZeroIsError);
        var runs = await Page(records, NonZeroIsError with { Kind = RecordKind.Run });
        var errors = await Page(records, NonZeroIsError with { Level = RecordLevelFilter.Error });
        var found = await Page(records, NonZeroIsError with { Search = "serve" });

        Assert.Equal(
            [("/code/d/broken.command", LogLevel.Error), ("/code/c/serve.command", LogLevel.Info)],
            page.Records.Take(2).Select(record => (record.Title, record.Level)));
        Assert.Equal(4, runs.Records.Count);
        Assert.Equal(["/code/d/broken.command", "/code/a/dev.command"], errors.Records.Select(record => record.Title));
        Assert.Equal("/code/c/serve.command", Assert.Single(found.Records).Title);

        var running = Assert.IsType<RunRecordDetail>(await records.ReadRecordDetailAsync(RecordKind.Run, page.Records[1].Id, nonZeroExitIsError: true, cancellation: TestContext.Current.CancellationToken));
        Assert.Equal((At(8), LogLevel.Info), (running.Time, running.Level));
        Assert.Null(running.EndState);
        Assert.Null(running.ImportedAt);
        Assert.Null(running.Output);
        Assert.False(running.Settled);
        var broken = Assert.IsType<RunRecordDetail>(await records.ReadRecordDetailAsync(RecordKind.Run, page.Records[0].Id, nonZeroExitIsError: true, cancellation: TestContext.Current.CancellationToken));
        Assert.Equal(("failed", null, null), (broken.EndState, broken.Pid, broken.Output));
    }

    [Fact]
    public async Task Pages_runs_and_other_records_together_from_the_last_record_shown()
    {
        using var records = new RecordStore(_dir, SessionStart);
        for (var index = 0; index < 60; index++)
        {
            Log(records, At(index), "info", $"line {index}");
            await records.AddRunAsync(new RunRecord(Session, index, SessionStart.AddMinutes(index), $"/s/{index}.command", null, null, null));
        }

        var first = await Page(records, RecordsQuery.All);
        var last = first.Records[^1];
        var second = await Page(records, RecordsQuery.All with { After = new RecordCursor(last.Time, last.Kind, last.Id) });

        // At one instant a run comes before a log line, as the kinds' names order them.
        Assert.Equal([RecordKind.Run, RecordKind.Log], first.Records.Take(2).Select(record => record.Kind));
        Assert.True(first.More);
        Assert.Equal(20, second.Records.Count);
        Assert.False(second.More);
        Assert.Empty(first.Records.Select(record => record.Key).Intersect(second.Records.Select(record => record.Key)));
    }

    [Fact]
    public async Task Lists_every_launch_that_has_records_newest_first()
    {
        using (var earlier = new RecordStore(_dir, SessionStart.AddDays(-1)))
            Log(earlier, At(-60), "info", "yesterday");
        using var records = await Seeded();
        // A launch whose only record is a run with no output yet.
        await records.AddRunAsync(Run(1, "/code/a/dev.command") with { Session = "2026-10-01T09:00:00.000Z" });

        var sources = await records.ReadRecordSourcesAsync(cancellation: TestContext.Current.CancellationToken);

        Assert.Equal(Session, sources.CurrentSession);
        Assert.Equal([Session, "2026-10-01T09:00:00.000Z", "2026-10-01T03:00:00.000Z"], sources.Sessions);
    }

    [Fact]
    public async Task HeldRecentIdentityResolutionDoesNotHoldRecordsWritesOrReadBarriers()
    {
        using var records = await Seeded();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var recent = records.ReadRecentAsync(path =>
        {
            entered.TrySetResult();
            release.Wait();
            return path;
        });
        Task? write = null;
        Task<RecordsPage>? page = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            write = records.AddRunEndAsync(new RunEnd(Session, 6, SessionStart.AddHours(1), "exited", 2));
            Log(records, At(8), "info", "written while path resolution is held");
            page = Page(records, RecordsQuery.All);
            await write.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var rows = await page.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Contains(rows.Records, row => row.Title == "written while path resolution is held");
            Assert.False(recent.IsCompleted);
        }
        finally
        {
            release.Set();
            await recent;
            if (write is not null) await write;
            if (page is not null) await page;
        }
    }

    [Fact]
    public async Task A_held_window_search_does_not_hold_writes_or_the_recent_list()
    {
        using var records = await Seeded();
        await Page(records, RecordsQuery.All); // Open the production reader before instrumenting it.
        // Test-only access avoids a runtime hook or widening the store's API. Override SQLite's LIKE
        // on this connection to hold the actual public search while its read transaction is live.
        var field = typeof(RecordStore).GetField("_windowConnection", BindingFlags.Instance | BindingFlags.NonPublic);
        var connection = Assert.IsType<SqliteConnection>(field?.GetValue(records));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        connection.CreateFunction<string, string, string, bool>("like", (_, _, _) =>
        {
            entered.TrySetResult();
            release.Wait();
            return true;
        });
        var search = Page(records, RecordsQuery.All with { Search = "held", Kind = RecordKind.Run });
        Task? write = null;
        Task? dismissal = null;
        Task<IReadOnlyList<RecentRun>>? recentRead = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            write = records.AddRunAsync(Run(8, "/code/new/dev.command"));
            await write.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            dismissal = records.AddDismissalAsync("/code/a/dev.command");
            await dismissal.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            recentRead = records.ReadRecentAsync();
            var recent = await recentRead.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

            Assert.Contains(recent, row => row.Path == "/code/new/dev.command");
            Assert.DoesNotContain(recent, row => row.Path == "/code/a/dev.command");
            Assert.False(search.IsCompleted); // Progress above happened while the SQL read was held.
        }
        finally
        {
            release.Set();
            await search;
            if (write is not null) await write;
            if (dismissal is not null) await dismissal;
            if (recentRead is not null) await recentRead;
            connection.CreateFunction<string, string, string, bool>("like", null);
        }
    }

    [Fact]
    public async Task Signals_after_each_stored_record_and_never_after_a_read()
    {
        using var records = new RecordStore(_dir, SessionStart);
        var stored = 0;
        records.Stored += () => Interlocked.Increment(ref stored);

        Log(records, At(1), "info", "one");
        await records.AddRunAsync(Run(1, "/code/a/dev.command"));
        records.Flush();
        Assert.Equal(2, Volatile.Read(ref stored));

        await records.ReadRecordsPageAsync(RecordsQuery.All, cancellation: TestContext.Current.CancellationToken);
        await records.ReadRecordSourcesAsync(cancellation: TestContext.Current.CancellationToken);
        await records.ReadRecordDetailAsync(RecordKind.Log, 1, nonZeroExitIsError: false, cancellation: TestContext.Current.CancellationToken);
        await records.ReadRecentAsync();
        records.Flush();
        Assert.Equal(2, Volatile.Read(ref stored));
    }
}
