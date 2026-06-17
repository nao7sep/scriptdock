using System;
using System.Collections.Generic;
using System.Linq;

namespace ScriptDock.Models;

/// <summary>
/// Computes the shortest unambiguous label for each script path in a set — no fragile
/// "is the parent dir named scripts" heuristic. Two passes:
/// <list type="number">
/// <item>Minimal unique trailing-suffix: start from the file name and, for any colliding
/// group, prepend one more parent segment — round by round — until every label is unique.
/// Always converges because the input paths are distinct.</item>
/// <item>Validated compaction: drop a shared interior segment (e.g. <c>scripts</c>) only when
/// the whole label set stays unique afterward, so it can never create an unresolvable
/// collision — at worst a label keeps one extra segment.</item>
/// </list>
/// Pure; result is independent of input order.
/// </summary>
public static class ScriptLabels
{
    public static IReadOnlyDictionary<string, string> Build(IEnumerable<string> paths)
    {
        var segments = paths
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(p => p, Split, StringComparer.Ordinal);

        var depth = segments.Keys.ToDictionary(p => p, _ => 1, StringComparer.Ordinal);

        bool deepened;
        do
        {
            deepened = false;
            var labels = segments.ToDictionary(kv => kv.Key, kv => Suffix(kv.Value, depth[kv.Key]), StringComparer.Ordinal);
            foreach (var group in labels.GroupBy(kv => kv.Value, kv => kv.Key))
            {
                if (group.Count() == 1)
                    continue;

                foreach (var path in group)
                {
                    if (depth[path] < segments[path].Length)
                    {
                        depth[path]++;
                        deepened = true;
                    }
                }
            }
        }
        while (deepened);

        var result = segments.ToDictionary(kv => kv.Key, kv => Suffix(kv.Value, depth[kv.Key]), StringComparer.Ordinal);
        return Compact(result);
    }

    private static string[] Split(string path) =>
        path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static string Suffix(string[] segments, int depth)
    {
        var take = Math.Min(depth, segments.Length);
        return take == 0 ? string.Empty : string.Join('/', segments[^take..]);
    }

    // Greedily drop a shared interior segment value, accepting a drop only when every label
    // stays unique afterward. Repeats until no safe drop remains; never loosens uniqueness.
    private static Dictionary<string, string> Compact(Dictionary<string, string> labels)
    {
        var current = new Dictionary<string, string>(labels, StringComparer.Ordinal);

        bool changed;
        do
        {
            changed = false;
            foreach (var candidate in InteriorSegments(current.Values))
            {
                var trial = current.ToDictionary(
                    kv => kv.Key,
                    kv => DropInterior(kv.Value, candidate),
                    StringComparer.Ordinal);

                if (trial.Values.Distinct(StringComparer.Ordinal).Count() == trial.Count)
                {
                    current = trial;
                    changed = true;
                    break;
                }
            }
        }
        while (changed);

        return current;
    }

    private static IEnumerable<string> InteriorSegments(IEnumerable<string> labels)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var label in labels)
        {
            var parts = label.Split('/');
            for (var i = 1; i < parts.Length - 1; i++)
                seen.Add(parts[i]);
        }
        return seen;
    }

    private static string DropInterior(string label, string value)
    {
        var parts = label.Split('/').ToList();
        for (var i = parts.Count - 2; i >= 1; i--)
        {
            if (string.Equals(parts[i], value, StringComparison.Ordinal))
                parts.RemoveAt(i);
        }
        return string.Join('/', parts);
    }
}
