using System;
using System.Collections.Generic;
using System.Linq;

namespace ScriptDock.Models;

/// <summary>
/// The difference between a fresh scan's found set and the set acknowledged at the last
/// scan: <see cref="Added"/> are newly appeared scripts (shown as new), <see
/// cref="Removed"/> are ones that have since disappeared. Comparison is by exact path.
/// </summary>
public sealed record ScanDiff(IReadOnlyList<string> Added, IReadOnlyList<string> Removed)
{
    public static ScanDiff Compute(IEnumerable<string> found, IEnumerable<string> known)
    {
        var foundSet = new HashSet<string>(found, StringComparer.Ordinal);
        var knownSet = new HashSet<string>(known, StringComparer.Ordinal);

        var added = foundSet.Where(p => !knownSet.Contains(p)).OrderBy(p => p, StringComparer.Ordinal).ToList();
        var removed = knownSet.Where(p => !foundSet.Contains(p)).OrderBy(p => p, StringComparer.Ordinal).ToList();

        return new ScanDiff(added, removed);
    }
}
