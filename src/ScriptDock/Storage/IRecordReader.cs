using System;
using System.Threading;
using System.Threading.Tasks;
using ScriptDock.Models;

namespace ScriptDock.Storage;

/// <summary>
/// What the Records window reads from <c>records.sqlite3</c>, and the signal that a record was stored.
/// Exists so the window can be tested against an in-memory reader. A read writes nothing, so it never
/// raises <see cref="Stored"/>: an open window that re-reads on that signal would otherwise read forever.
/// </summary>
public interface IRecordReader
{
    /// <summary>This process launch, by its start time.</summary>
    string Session { get; }

    /// <summary>A filtered page of summaries, newest first. Cancelling stops a read that is no longer wanted.</summary>
    Task<RecordsPage> ReadRecordsPageAsync(RecordsQuery query, CancellationToken cancellationToken = default);

    /// <summary>One record whole, or null when there is no such record. A run's level follows
    /// <paramref name="nonZeroExitIsError"/> as a page's does (<see cref="RecordsQuery.NonZeroExitIsError"/>).</summary>
    Task<RecordDetail?> ReadRecordDetailAsync(RecordKind kind, long id, bool nonZeroExitIsError = false,
        CancellationToken cancellationToken = default);

    /// <summary>Copies a run's whole output, beyond what its detail shows, into <paramref name="destination"/>,
    /// in pieces: the stored record, or the run's own file when the output was too large to store. False when
    /// the run has neither.</summary>
    Task<bool> ReadRunOutputAsync(long id, System.IO.Stream destination);

    /// <summary>Every launch that has a record the window lists.</summary>
    Task<RecordSources> ReadRecordSourcesAsync();

    /// <summary>Raised, on the records' own thread, after each entry the database stored. An entry that went
    /// to the fallback file is not in the database, so it raises nothing.</summary>
    event Action? Stored;
}
