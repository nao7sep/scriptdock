using System;
using System.Collections.Generic;
using System.Linq;
using ScriptDock.Models;

namespace ScriptDock.Services;

/// <summary>
/// Turns a scan result and the user's preferences into the Scripts list: sorted by absolute
/// path (so a repo's scripts group together) while showing the dedup label, flagged
/// new/removed for the colour cue and running for the tile dot, with hidden
/// scripts filtered out unless the user is showing them. Removed scripts are surfaced
/// regardless of the hidden filter so a disappearance is noticed. Display names come from the
/// caller-supplied label map (<see cref="ScriptLabels"/>). Pure — no I/O, no UI.
/// </summary>
public static class ScriptListBuilder
{
    public static IReadOnlyList<ScriptItem> BuildScripts(
        IReadOnlyCollection<string> found,
        IReadOnlyCollection<string> removed,
        ISet<string> hidden,
        ISet<string> newPaths,
        ISet<string> runningPaths,
        IReadOnlyDictionary<string, string> labels,
        bool showHidden)
    {
        var items = new List<ScriptItem>();

        // Build every tile through one factory so a new ScriptItem field can't be set on the found
        // path and forgotten on the removed one — the only difference between the two is the flag.
        ScriptItem Tile(string path, ScriptFlag flag) => new(path)
        {
            DisplayName = ScriptLabels.LabelFor(labels, path),
            IsHidden = hidden.Contains(path),
            IsRunning = runningPaths.Contains(path),
            Flag = flag,
        };

        foreach (var path in found)
        {
            if (hidden.Contains(path) && !showHidden)
                continue;

            items.Add(Tile(path, newPaths.Contains(path) ? ScriptFlag.New : ScriptFlag.None));
        }

        // Removed scripts are surfaced regardless of the hidden filter so a disappearance is noticed.
        foreach (var path in removed)
            items.Add(Tile(path, ScriptFlag.Removed));

        // Sort by the absolute path, not the dedup label: the label is a minimal-unique suffix
        // that doesn't sort intuitively, whereas the path groups a repo's scripts together. The
        // row still shows the label.
        return items
            .OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Path, StringComparer.Ordinal)
            .ToList();
    }
}
