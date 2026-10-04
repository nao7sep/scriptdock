using System;
using System.Collections.Generic;
using System.Linq;
using ScriptDock.Models;

namespace ScriptDock.Services;

/// <summary>
/// The Recent list, newest first, a path appearing once (by physical identity) and the list capped. Pure:
/// <see cref="From"/> reads it out of the run and dismissal records; <see cref="Add"/> keeps the list in
/// memory current as a run is recorded.
/// </summary>
public static class RecentRuns
{
    // High safety bound only — curation is by dismissal, not eviction (the Recent list is the
    // user's auto-favorites, kept until explicitly dismissed).
    public const int DefaultMax = 500;

    /// <summary>Each script's latest run, with its recorded end, unless a dismissal came at or after it.</summary>
    public static List<RecentRun> From(
        IEnumerable<RecentRun> runs,
        IEnumerable<(string Path, DateTimeOffset At)> dismissals,
        int max = DefaultMax)
    {
        var dismissedAt = new Dictionary<string, DateTimeOffset>(PathIdentity.Comparer);
        foreach (var (path, at) in dismissals)
        {
            var key = PathIdentity.Key(path);
            if (!dismissedAt.TryGetValue(key, out var latest) || at > latest)
                dismissedAt[key] = at;
        }

        var latestRun = new Dictionary<string, RecentRun>(PathIdentity.Comparer);
        foreach (var run in runs)
        {
            var key = PathIdentity.Key(run.Path);
            if (!latestRun.TryGetValue(key, out var latest) || run.RanAt > latest.RanAt)
                latestRun[key] = run;
        }

        return latestRun
            .Where(pair => !dismissedAt.TryGetValue(pair.Key, out var dismissed) || dismissed < pair.Value.RanAt)
            .Select(pair => pair.Value)
            .OrderByDescending(run => run.RanAt)
            .Take(max)
            .ToList();
    }

    public static List<RecentRun> Add(IReadOnlyList<RecentRun> existing, string path, DateTimeOffset ranAt, int max = DefaultMax)
    {
        var result = new List<RecentRun> { new() { Path = path, RanAt = ranAt } };
        result.AddRange(existing.Where(r => !PathIdentity.Same(r.Path, path)));

        if (result.Count > max)
            result.RemoveRange(max, result.Count - max);

        return result;
    }
}
