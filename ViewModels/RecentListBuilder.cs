using System;
using System.Collections.Generic;
using System.Linq;
using ScriptDock.Models;
using ScriptDock.Services;

namespace ScriptDock.ViewModels;

/// <summary>
/// Merges the persisted recently-run list with the live process list into the Recent list —
/// one <see cref="RecentEntry"/> per script path, newest first. A path's live process (a running
/// one preferred, else the newest) is attached so the entry shows running state and output; a
/// recent with no live process shows idle. A live run whose path is <em>not</em> in the recent
/// list (e.g. a recaptured process whose recent entry was evicted) is still surfaced, so a running
/// script is never invisible or uncontrollable. Display names come from the caller-supplied label
/// map so a script reads the same here and in the Scripts list. Pure — no I/O, no UI.
/// </summary>
public static class RecentListBuilder
{
    public static IReadOnlyList<RecentEntry> Build(
        IReadOnlyList<RecentRun> recents,
        IReadOnlyList<ScriptProcess> active,
        IReadOnlyDictionary<string, string> labels)
    {
        var byPath = active
            .GroupBy(p => p.ScriptPath, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.FirstOrDefault(p => p.State == RunState.Running) ?? g.OrderByDescending(p => p.StartedAt).First(),
                StringComparer.Ordinal);

        var entries = new List<RecentEntry>(recents.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var run in recents) // RecentRuns keeps this newest-first
        {
            seen.Add(run.Path);
            byPath.TryGetValue(run.Path, out var process);
            entries.Add(new RecentEntry(run.Path, ScriptLabels.LabelFor(labels, run.Path), run.RanAt, process));
        }

        // Append any live process the recent list didn't account for, newest run first, so a
        // recaptured (or otherwise un-listed) running script is still shown and controllable.
        foreach (var process in byPath.Values
                     .Where(p => !seen.Contains(p.ScriptPath))
                     .OrderByDescending(p => p.StartedAt))
        {
            entries.Add(new RecentEntry(
                process.ScriptPath, ScriptLabels.LabelFor(labels, process.ScriptPath), process.StartedAt, process));
        }

        return entries;
    }
}
