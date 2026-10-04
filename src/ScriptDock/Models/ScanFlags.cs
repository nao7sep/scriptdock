using System;
using System.Collections.Generic;
using System.Linq;
using ScriptDock.Services;

namespace ScriptDock.Models;

/// <summary>
/// The new and removed flags the Scripts pane shows after a scan, new ones held by physical identity. A
/// full scan (launch, Rescan) flags exactly its own diff, ending every earlier flag. A background scan,
/// made when the main window comes back to the front, keeps the flags already shown and adds its own,
/// except that a script found again is no longer removed and one now gone is no longer new.
/// </summary>
public sealed record ScanFlags(HashSet<string> NewKeys, IReadOnlyList<string> Removed)
{
    public static ScanFlags Full(ScanDiff diff) => new(Keys(diff.Added), diff.Removed);

    public static ScanFlags Background(
        ScanDiff diff, IEnumerable<string> found, IEnumerable<string> newKeys, IEnumerable<string> removed)
    {
        var foundKeys = Keys(found);
        var removedNow = Keys(diff.Removed);

        var keptNew = newKeys.Where(foundKeys.Contains);
        var keptRemoved = removed.Where(path =>
        {
            var key = PathIdentity.Key(path);
            return !foundKeys.Contains(key) && !removedNow.Contains(key);
        });

        return new ScanFlags(
            new HashSet<string>(keptNew.Concat(diff.Added.Select(PathIdentity.Key)), PathIdentity.Comparer),
            keptRemoved.Concat(diff.Removed).OrderBy(path => path, StringComparer.Ordinal).ToList());
    }

    private static HashSet<string> Keys(IEnumerable<string> paths) =>
        new(paths.Select(PathIdentity.Key), PathIdentity.Comparer);
}
