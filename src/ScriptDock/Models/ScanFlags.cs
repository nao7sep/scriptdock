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
    public static ScanFlags Full(ScanDiff diff, Func<string, string>? key = null) => new(Keys(diff.Added, key ?? PathIdentity.Key), diff.Removed);

    public static ScanFlags Background(
        ScanDiff diff, IEnumerable<string> found, IEnumerable<string> newKeys, IEnumerable<string> removed,
        Func<string, string>? key = null)
    {
        key ??= PathIdentity.Key;
        var foundKeys = Keys(found, key);
        var removedNow = Keys(diff.Removed, key);

        var keptNew = newKeys.Where(foundKeys.Contains);
        var keptRemoved = removed.Where(path =>
        {
            var identity = key(path);
            return !foundKeys.Contains(identity) && !removedNow.Contains(identity);
        });

        return new ScanFlags(
            new HashSet<string>(keptNew.Concat(diff.Added.Select(key)), PathIdentity.Comparer),
            keptRemoved.Concat(diff.Removed).OrderBy(path => path, StringComparer.Ordinal).ToList());
    }

    private static HashSet<string> Keys(IEnumerable<string> paths, Func<string, string> key) =>
        new(paths.Select(key), PathIdentity.Comparer);
}
