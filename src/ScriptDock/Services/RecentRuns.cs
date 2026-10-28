using System;
using System.Collections.Generic;
using System.Linq;
using ScriptDock.Models;

namespace ScriptDock.Services;

/// <summary>
/// The Recent list, newest first, a path appearing once (by physical identity), kept until dismissed. Pure:
/// <see cref="From"/> reads it out of the run and dismissal records; <see cref="Add"/> keeps the list in
/// memory current as a run is recorded.
/// </summary>
public static class RecentRuns
{
    /// <summary>Each script's latest run, with its recorded end, unless a dismissal came at or after it.</summary>
    public static List<RecentRun> From(
        IEnumerable<RecentRun> runs,
        IEnumerable<(string Path, DateTimeOffset At)> dismissals,
        Func<string, string>? pathKey = null)
    {
        pathKey ??= PathIdentity.Key;
        var dismissedAt = new Dictionary<string, DateTimeOffset>(PathIdentity.Comparer);
        foreach (var (path, at) in dismissals)
        {
            var key = pathKey(path);
            if (!dismissedAt.TryGetValue(key, out var latest) || at > latest)
                dismissedAt[key] = at;
        }

        var latestRun = new Dictionary<string, RecentRun>(PathIdentity.Comparer);
        foreach (var run in runs)
        {
            var key = pathKey(run.Path);
            if (!latestRun.TryGetValue(key, out var latest) || run.RanAt > latest.RanAt)
                latestRun[key] = run;
        }

        return latestRun
            .Where(pair => !dismissedAt.TryGetValue(pair.Key, out var dismissed) || dismissed < pair.Value.RanAt)
            .Select(pair => pair.Value)
            .OrderByDescending(run => run.RanAt)
            .ToList();
    }

    public static List<RecentRun> Add(IReadOnlyList<RecentRun> existing, string path, DateTimeOffset ranAt,
        Func<string, string>? key = null, string? scriptKey = null)
    {
        key ??= PathIdentity.Key;
        scriptKey ??= key(path);
        var result = new List<RecentRun> { new() { Path = path, RanAt = ranAt } };
        result.AddRange(existing.Where(r => !PathIdentity.Comparer.Equals(key(r.Path), scriptKey)));
        return result;
    }
}
