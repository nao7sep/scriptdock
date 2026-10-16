using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ScriptDock.Models;
using ScriptDock.Storage;

namespace ScriptDock.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IRecordReader"/>. Each read answers from the next queued answer, or from the
/// standing answer when none is queued, and every read is kept so a test can say what was asked.
/// </summary>
public sealed class FakeRecordReader : IRecordReader
{
    public const string CurrentSession = "2026-10-04T08:00:00.000Z";

    private readonly Queue<Func<RecordsQuery, Task<RecordsPage>>> _pages = new();

    public string Session => CurrentSession;

    public List<RecordsQuery> PageQueries { get; } = [];
    public List<(RecordKind Kind, long Id)> DetailReads { get; } = [];
    public int SourceReads { get; private set; }
    public List<long> OutputReads { get; } = [];

    public RecordsPage Page { get; set; } = new([], false);
    public Func<RecordKind, long, Task<RecordDetail?>> Detail { get; set; } = (_, _) => Task.FromResult<RecordDetail?>(null);
    public Func<long, Task<byte[]?>> Output { get; set; } = _ => Task.FromResult<byte[]?>(null);
    public Func<Task<RecordSources>> Sources { get; set; } =
        () => Task.FromResult(new RecordSources(CurrentSession, [CurrentSession]));

    public RecordsQuery LastQuery => PageQueries[^1];

    public bool IsListening => Stored is not null;

    public event Action? Stored;

    public void RaiseStored() => Stored?.Invoke();

    public void Next(RecordsPage page) => _pages.Enqueue(_ => Task.FromResult(page));

    public void Next(Task<RecordsPage> page) => _pages.Enqueue(_ => page);

    public void NextFails() => _pages.Enqueue(_ => Task.FromException<RecordsPage>(new InvalidOperationException("busy (test)")));

    public Task<RecordsPage> ReadRecordsPageAsync(RecordsQuery query)
    {
        PageQueries.Add(query);
        return _pages.TryDequeue(out var answer) ? answer(query) : Task.FromResult(Page);
    }

    public Task<RecordDetail?> ReadRecordDetailAsync(RecordKind kind, long id)
    {
        DetailReads.Add((kind, id));
        return Detail(kind, id);
    }

    public async Task<bool> ReadRunOutputAsync(long id, Action<System.IO.Stream> read)
    {
        OutputReads.Add(id);
        if (await Output(id) is not { } output)
            return false;
        using var stream = new System.IO.MemoryStream(output, writable: false);
        read(stream);
        return true;
    }

    public Task<RecordSources> ReadRecordSourcesAsync()
    {
        SourceReads++;
        return Sources();
    }
}
