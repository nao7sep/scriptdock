using System;
using System.Collections.Generic;
using System.Linq;
using ScriptDock.Models;

namespace ScriptDock.Services;

/// <summary>
/// Turns a scan result and the user's preferences into the script and favorite lists:
/// sorted by display name, flagged new/removed for the colour cue, with hidden scripts
/// filtered out unless the user is showing them. Removed scripts are surfaced regardless
/// of the hidden filter so a disappearance is noticed. Pure — no I/O, no UI.
/// </summary>
public static class ScriptListBuilder
{
    public static IReadOnlyList<ScriptItem> BuildScripts(
        IReadOnlyCollection<string> found,
        IReadOnlyCollection<string> removed,
        ISet<string> favorites,
        ISet<string> hidden,
        ISet<string> newPaths,
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
                IsFavorite = favorites.Contains(path),
                IsHidden = isHidden,
                Flag = newPaths.Contains(path) ? ScriptFlag.New : ScriptFlag.None,
            });
        }

        foreach (var path in removed)
        {
            items.Add(new ScriptItem(path)
            {
                IsFavorite = favorites.Contains(path),
                IsHidden = hidden.Contains(path),
                Flag = ScriptFlag.Removed,
            });
        }

        return Sorted(items);
    }

    public static IReadOnlyList<ScriptItem> BuildFavorites(
        IReadOnlyCollection<string> favorites,
        ISet<string> found)
    {
        var items = favorites.Select(path => new ScriptItem(path)
        {
            IsFavorite = true,
            Flag = found.Contains(path) ? ScriptFlag.None : ScriptFlag.Removed,
        });

        return Sorted(items);
    }

    private static IReadOnlyList<ScriptItem> Sorted(IEnumerable<ScriptItem> items) =>
        items.OrderBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase)
             .ThenBy(i => i.Path, StringComparer.Ordinal)
             .ToList();
}
