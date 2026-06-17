using System;
using System.Collections.Generic;
using System.Linq;
using ScriptDock.Models;

namespace ScriptDock.Services;

/// <summary>
/// Maintains the recently-run list: the just-run script moves to the front, any earlier
/// entry for the same path is removed (so a path appears once), and the list is capped.
/// Pure — returns a new list, newest first.
/// </summary>
public static class RecentRuns
{
    public const int DefaultMax = 50;

    public static List<RecentRun> Add(IReadOnlyList<RecentRun> existing, string path, DateTimeOffset ranAt, int max = DefaultMax)
    {
        var result = new List<RecentRun> { new() { Path = path, RanAt = ranAt } };
        result.AddRange(existing.Where(r => !string.Equals(r.Path, path, StringComparison.Ordinal)));

        if (result.Count > max)
            result.RemoveRange(max, result.Count - max);

        return result;
    }
}
