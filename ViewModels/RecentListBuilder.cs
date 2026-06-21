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
/// recent with no live process shows idle. Display names come from the caller-supplied label
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
        foreach (var run in recents) // RecentRuns keeps this newest-first
        {
            byPath.TryGetValue(run.Path, out var process);
            var name = labels.TryGetValue(run.Path, out var label) ? label : System.IO.Path.GetFileName(run.Path);
            entries.Add(new RecentEntry(run.Path, name, run.RanAt, process));
        }

        return entries;
    }
}
