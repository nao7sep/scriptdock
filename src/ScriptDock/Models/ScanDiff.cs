using System;
using System.Collections.Generic;
using System.Linq;
using ScriptDock.Services;

namespace ScriptDock.Models;

/// <summary>
/// The difference between a fresh scan's found set and the set acknowledged at the last
/// scan: <see cref="Added"/> are newly appeared scripts (shown as new), <see
/// cref="Removed"/> are ones that have since disappeared. Comparison is by physical identity.
/// </summary>
public sealed record ScanDiff(IReadOnlyList<string> Added, IReadOnlyList<string> Removed)
{
    public static ScanDiff Compute(IEnumerable<string> found, IEnumerable<string> known)
    {
        var foundByKey = found.GroupBy(PathIdentity.Key, PathIdentity.Comparer)
            .ToDictionary(group => group.Key, group => group.First(), PathIdentity.Comparer);
        var knownByKey = known.GroupBy(PathIdentity.Key, PathIdentity.Comparer)
            .ToDictionary(group => group.Key, group => group.First(), PathIdentity.Comparer);

        var added = foundByKey.Where(pair => !knownByKey.ContainsKey(pair.Key))
            .Select(pair => pair.Value).OrderBy(path => path, StringComparer.Ordinal).ToList();
        var removed = knownByKey.Where(pair => !foundByKey.ContainsKey(pair.Key))
            .Select(pair => pair.Value).OrderBy(path => path, StringComparer.Ordinal).ToList();

        return new ScanDiff(added, removed);
    }
}
