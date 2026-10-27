using System;
using System.Collections.Generic;
using System.IO;
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
        var id = Assert.Single((await Wait(store.ReadRecordsPageAsync(RecordsQuery.All))).Records).Id;
        var detail = Assert.IsType<RunRecordDetail>(await Wait(store.ReadRecordDetailAsync(RecordKind.Run, id)));
        Assert.True(detail.OutputFromFile);
        Assert.Equal("new descendant output", Encoding.UTF8.GetString(detail.Output!));
        Assert.Equal("new descendant output", await Export(store, id));

        File.Delete(path);
        detail = Assert.IsType<RunRecordDetail>(await Wait(store.ReadRecordDetailAsync(RecordKind.Run, id)));
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
        var id = Assert.Single((await Wait(store.ReadRecordsPageAsync(RecordsQuery.All))).Records).Id;
        var mode = File.GetUnixFileMode(path);
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserWrite);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Wait(store.ReadRecordDetailAsync(RecordKind.Run, id)));
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
