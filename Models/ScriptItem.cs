using System;

namespace ScriptDock.Models;

/// <summary>
/// A script as shown in a list: its path, the user flags on it (favorite, hidden), and
/// its since-last-scan flag (new/removed) for the colour cue. Built by
/// <c>ScriptListBuilder</c>; immutable once built.
/// </summary>
public sealed class ScriptItem
{
    public ScriptItem(string path) => Path = path;

    public string Path { get; }
    public bool IsFavorite { get; init; }
    public bool IsHidden { get; init; }
    public ScriptFlag Flag { get; init; }

    /// <summary>A compact label: <c>app/script</c> for the conventional
    /// <c>&lt;app&gt;/scripts/&lt;name&gt;</c> layout, otherwise the file name.</summary>
    public string DisplayName
    {
        get
        {
            var name = System.IO.Path.GetFileName(Path);
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (dir is null)
                return name;

            if (string.Equals(System.IO.Path.GetFileName(dir), "scripts", StringComparison.OrdinalIgnoreCase))
            {
                var app = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(dir) ?? string.Empty);
                if (!string.IsNullOrEmpty(app))
                    return $"{app}/{name}";
            }

            return name;
        }
    }
}
