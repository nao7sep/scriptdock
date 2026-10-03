using System;
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

    /// <summary>A filtered page of summaries, newest first.</summary>
    Task<RecordsPage> ReadRecordsPageAsync(RecordsQuery query);

    /// <summary>One record whole, or null when there is no such record.</summary>
    Task<RecordDetail?> ReadRecordDetailAsync(RecordKind kind, long id);

    /// <summary>Every launch that has a record the window lists.</summary>
    Task<RecordSources> ReadRecordSourcesAsync();

    /// <summary>Raised, on the records' own thread, after each entry the database stored. An entry that went
    /// to the fallback file is not in the database, so it raises nothing.</summary>
    event Action? Stored;
}
