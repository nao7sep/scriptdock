using System.Collections.Generic;
using System.Threading.Tasks;
using ScriptDock.Models;

namespace ScriptDock.Storage;

/// <summary>
/// The app's records, per the data-lifecycle-conventions' Records section. Exists so the view model and the
/// runner can be tested against an in-memory store instead of the database. A write that cannot reach the
/// database still keeps its entry (see <see cref="RecordStore"/>); the awaited writes then fault so the
/// caller can say so.
/// </summary>
public interface IRecordStore
{
    /// <summary>This process launch, by its start time, as every record written now carries it.</summary>
    string Session { get; }

    /// <summary>True when the database could not be opened this session. Every write then lands in the
    /// fallback file and faults; the app has already said once that run history is not being recorded,
    /// so callers carry on rather than reporting each write.</summary>
    bool DatabaseUnavailable => false;

    /// <summary>Records a started run.</summary>
    Task AddRunAsync(RunRecord run);

    /// <summary>Records a run's end.</summary>
    Task AddRunEndAsync(RunEnd end);

    /// <summary>Records that the user dismissed a script from the Recent list.</summary>
    Task AddDismissalAsync(string scriptPath);

    /// <summary>The Recent list: each script's latest run that no later dismissal took off the list.</summary>
    Task<IReadOnlyList<RecentRun>> ReadRecentAsync();

    /// <summary>Records one scan's report.</summary>
    void AddScanReport(ScanReport report);

    /// <summary>The recorded runs whose output went to one of <paramref name="outputPaths"/>, keyed by that path.</summary>
    Task<IReadOnlyDictionary<string, RunRecord>> FindRunsByOutputPathAsync(IReadOnlyCollection<string> outputPaths);

    /// <summary>Stores a run's whole output, replacing an earlier import of the same run.</summary>
    Task AddRunOutputAsync(RunRecord run, byte[] output);
}
