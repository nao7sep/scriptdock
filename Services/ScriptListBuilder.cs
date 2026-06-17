using System;
using System.Collections.Generic;
using System.Linq;
using ScriptDock.Models;

namespace ScriptDock.Services;

/// <summary>
/// Turns a scan result and the user's preferences into the Scripts list: sorted by display
/// name, flagged new/removed for the colour cue and running for the tile dot, with hidden
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

        foreach (var path in found)
        {
            var isHidden = hidden.Contains(path);
            if (isHidden && !showHidden)
                continue;

            items.Add(new ScriptItem(path)
            {
                DisplayName = Label(labels, path),
                IsHidden = isHidden,
                IsRunning = runningPaths.Contains(path),
                Flag = newPaths.Contains(path) ? ScriptFlag.New : ScriptFlag.None,
            });
        }

        foreach (var path in removed)
        {
            items.Add(new ScriptItem(path)
            {
                DisplayName = Label(labels, path),
                IsHidden = hidden.Contains(path),
                IsRunning = runningPaths.Contains(path),
                Flag = ScriptFlag.Removed,
            });
        }

        return items
            .OrderBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Path, StringComparer.Ordinal)
            .ToList();
    }

    private static string Label(IReadOnlyDictionary<string, string> labels, string path) =>
        labels.TryGetValue(path, out var name) ? name : System.IO.Path.GetFileName(path);
}
