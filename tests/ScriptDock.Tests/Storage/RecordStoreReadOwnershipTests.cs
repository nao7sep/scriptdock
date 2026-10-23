using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ScriptDock.Models;
using ScriptDock.Storage;
using ScriptDock.Tests.Fakes;
using Xunit;

namespace ScriptDock.Tests.Storage;

public sealed class RecordStoreReadOwnershipTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 23, 4, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "scriptdock-read-ownership-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;
    private static Task Wait(Task task) => task.WaitAsync(TimeSpan.FromSeconds(20), Cancellation);
    private static Task<T> Wait<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(20), Cancellation);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private RunRecord Run(string? path = null) => new("2026-10-23T04:00:00.000Z", 1, Start, "/fixture/script.command", null, null,
        path ?? Path.Combine(_directory, "missing.log"));

    private static object Private(RecordStore store, string field) =>
        typeof(RecordStore).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;

    [Fact]
    public async Task Superseded_reads_are_removed_physically_while_the_active_query_is_held()
    {
        using var store = new RecordStore(_directory, Start);
        await Wait(store.AddRunAsync(Run()));
        await Wait(store.ReadRecordsPageAsync(RecordsQuery.All, Cancellation));
        var connection = (SqliteConnection)Private(store, "_windowConnection");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        connection.CreateFunction<string, string, string, bool>("like", (_, _, _) =>
        {
            entered.TrySetResult();
            release.Wait(Cancellation);
            return true;
        });
        var sources = new List<CancellationTokenSource> { new() };
        var reads = new List<Task<RecordsPage>>
        {
            store.ReadRecordsPageAsync(RecordsQuery.All with { Search = "held", Kind = RecordKind.Run }, sources[0].Token),
        };
        try
        {
            await Wait(entered.Task);
            for (var index = 0; index < 100; index++)
            {
                sources[^1].Cancel();
                sources.Add(new CancellationTokenSource());
                reads.Add(store.ReadRecordsPageAsync(RecordsQuery.All, sources[^1].Token));
                lock (Private(store, "_windowGate"))
                    Assert.Single((IDictionary)Private(store, "_windowPending"));
            }
            Assert.False(reads[^1].IsCompleted);
            release.Set();
            foreach (var obsolete in reads.GetRange(0, reads.Count - 1))
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Wait(obsolete));
            Assert.Single((await Wait(reads[^1])).Records);
        }
        finally
        {
            release.Set();
            foreach (var source in sources) source.Cancel();
            foreach (var read in reads)
            {
                try { await Wait(read); }
                catch (OperationCanceledException) { }
            }
            connection.CreateFunction<string, string, string, bool>("like", null);
            foreach (var source in sources) source.Dispose();
        }
    }

    [Fact]
    public async Task Cancellation_interrupts_the_active_sqlite_query_and_frees_the_reader_for_its_successor()
    {
        using var store = new RecordStore(_directory, Start);
        await Wait(store.ReadRecordsPageAsync(RecordsQuery.All, Cancellation));
        var connection = (SqliteConnection)Private(store, "_windowConnection");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.CreateFunction("fixture_progress", () => { entered.TrySetResult(); return 0; });
        using var cancellation = new CancellationTokenSource();
        // Run through the production reader owner and its cancellation registration. The recursive
        // query has no finite result: only SQLite interruption can settle it and admit the next read.
        Func<SqliteConnection, long> query = db =>
        {
            using var command = db.CreateCommand();
            command.CommandText = "WITH RECURSIVE counter(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM counter) SELECT sum(n + fixture_progress()) FROM counter";
            return (long)command.ExecuteScalar()!;
        };
        var method = typeof(RecordStore).GetMethod("WindowRead", BindingFlags.Instance | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(long));
        var active = (Task<long>)method.Invoke(store, [query, cancellation.Token])!;
        try
        {
            await Wait(entered.Task);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Wait(active));
            Assert.Empty((await Wait(store.ReadRecordsPageAsync(RecordsQuery.All, Cancellation))).Records);
        }
        finally
        {
            // If an assertion fails, still release the real SQLite operation before disposing its owner.
            SQLitePCL.raw.sqlite3_interrupt(connection.Handle);
            try { await Wait(active); }
            catch (OperationCanceledException) { }
            connection.CreateFunction("fixture_progress", (Func<int>?)null);
        }
    }

    [Fact]
    public async Task Held_output_export_does_not_occupy_browsing_or_the_writer()
    {
        using var store = new RecordStore(_directory, Start);
        var run = Run();
        await Wait(store.AddRunAsync(run));
        await Wait(store.AddRunOutputAsync(run, Encoding.UTF8.GetBytes("whole output")));
        var id = Assert.Single((await Wait(store.ReadRecordsPageAsync(RecordsQuery.All, Cancellation))).Records).Id;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        string? output = null;
        var export = store.ReadRunOutputAsync(id, stream =>
        {
            entered.TrySetResult();
            release.Wait(Cancellation);
            using var reader = new StreamReader(stream, leaveOpen: true);
            output = reader.ReadToEnd();
        });
        try
        {
            await Wait(entered.Task);
            await Wait(store.AddRunEndAsync(new RunEnd(run.Session, run.Run, Start.AddSeconds(1), "exited", 7)));
            var page = await Wait(store.ReadRecordsPageAsync(RecordsQuery.All with { NonZeroExitIsError = true }, Cancellation));
            Assert.Equal(ScriptDock.Services.LogLevel.Error, Assert.Single(page.Records).Level);
            Assert.False(export.IsCompleted);
        }
        finally
        {
            release.Set();
            Assert.True(await Wait(export));
        }
        Assert.Equal("whole output", output);
    }

    [Fact]
    public async Task Output_file_revision_survives_reopening_and_a_later_revision_replaces_it()
    {
        var run = Run();
        var revision = new OutputFileRevision(17, Start.UtcTicks, Start.AddSeconds(-1).UtcTicks);
        using (var store = new RecordStore(_directory, Start))
        {
            await Wait(store.AddRunAsync(run));
            await Wait(store.RecordOutputFileRevisionAsync(run, revision with { Length = 3 }));
            await Wait(store.RecordOutputFileRevisionAsync(run, revision));
        }
        using var reopened = new RecordStore(_directory, Start.AddMinutes(1));
        Assert.Equal(revision, await Wait(reopened.ReadOutputFileRevisionAsync(run)));
        await Wait(reopened.AddRunOutputAsync(run, Encoding.UTF8.GetBytes("new snapshot")));
        Assert.Null(await Wait(reopened.ReadOutputFileRevisionAsync(run)));
    }

    [Fact]
    public async Task Original_output_file_wins_over_a_stale_snapshot_for_detail_and_export_and_missing_file_falls_back()
    {
        using var store = new RecordStore(_directory, Start);
        var path = Path.Combine(_directory, "output.log");
        var run = Run(path);
        await Wait(store.AddRunAsync(run));
        await Wait(store.AddRunOutputAsync(run, Encoding.UTF8.GetBytes("stale snapshot")));
        await File.WriteAllTextAsync(path, "new descendant output", Cancellation);
        var id = Assert.Single((await Wait(store.ReadRecordsPageAsync(RecordsQuery.All, Cancellation))).Records).Id;
        var detail = Assert.IsType<RunRecordDetail>(await Wait(store.ReadRecordDetailAsync(RecordKind.Run, id, false, Cancellation)));
        Assert.True(detail.OutputFromFile);
        Assert.Equal("new descendant output", Encoding.UTF8.GetString(detail.Output!));
        Assert.Equal("new descendant output", await Export(store, id));

        File.Delete(path);
        detail = Assert.IsType<RunRecordDetail>(await Wait(store.ReadRecordDetailAsync(RecordKind.Run, id, false, Cancellation)));
        Assert.False(detail.OutputFromFile);
        Assert.Equal("stale snapshot", Encoding.UTF8.GetString(detail.Output!));
        Assert.Equal("stale snapshot", await Export(store, id));
    }

    [MacOnlyFact]
    [SupportedOSPlatform("macos")]
    public async Task Denied_original_file_is_reported_instead_of_presenting_a_stale_snapshot()
    {
        using var store = new RecordStore(_directory, Start);
        var path = Path.Combine(_directory, "denied.log");
        var run = Run(path);
        await Wait(store.AddRunAsync(run));
        await Wait(store.AddRunOutputAsync(run, Encoding.UTF8.GetBytes("stale snapshot")));
        await File.WriteAllTextAsync(path, "authoritative output", Cancellation);
        var id = Assert.Single((await Wait(store.ReadRecordsPageAsync(RecordsQuery.All, Cancellation))).Records).Id;
        var mode = File.GetUnixFileMode(path);
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserWrite);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Wait(store.ReadRecordDetailAsync(RecordKind.Run, id, false, Cancellation)));
            var callbackCalled = false;
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Wait(store.ReadRunOutputAsync(id, _ => callbackCalled = true)));
            Assert.False(callbackCalled);
        }
        finally { File.SetUnixFileMode(path, mode); }
        Assert.Equal("authoritative output", await File.ReadAllTextAsync(path, Cancellation));
    }

    private static async Task<string?> Export(RecordStore store, long id)
    {
        string? output = null;
        Assert.True(await Wait(store.ReadRunOutputAsync(id, stream =>
        {
            using var reader = new StreamReader(stream, leaveOpen: true);
            output = reader.ReadToEnd();
        })));
        return output;
    }
}
