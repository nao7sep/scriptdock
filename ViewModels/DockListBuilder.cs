using System;
using System.Collections.Generic;
using System.Linq;
using ScriptDock.Models;
using ScriptDock.Services;

namespace ScriptDock.ViewModels;

/// <summary>
/// Merges the persisted recently-run list with the live process list into the Recent list —
/// one <see cref="DockEntry"/> per script path, newest first. A path's live process (a
/// running one preferred, else the newest) is attached so the entry shows running state and
/// output; a recent with no live process shows idle. Pure — no I/O, no UI.
/// </summary>
public static class DockListBuilder
{
    public static IReadOnlyList<DockEntry> Build(
        IReadOnlyList<RecentRun> recents,
        IReadOnlyList<ScriptProcess> active)
    {
        var byPath = active
            .GroupBy(p => p.ScriptPath, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.FirstOrDefault(p => p.State == RunState.Running) ?? g.OrderByDescending(p => p.StartedAt).First(),
                StringComparer.Ordinal);

        var entries = new List<DockEntry>(recents.Count);
        foreach (var run in recents) // RecentRuns keeps this newest-first
        {
            byPath.TryGetValue(run.Path, out var process);
            entries.Add(new DockEntry(run.Path, run.RanAt, process));
        }

        return entries;
    }
}
