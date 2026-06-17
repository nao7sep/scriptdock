namespace ScriptDock.Models;

/// <summary>
/// A script as shown in the Scripts list: its path, whether it is hidden, whether it is
/// currently running (for the tile's running dot), and its since-last-scan flag (new/removed)
/// for the colour cue. Built by <c>ScriptListBuilder</c>; immutable once built.
/// </summary>
public sealed class ScriptItem
{
    public ScriptItem(string path) => Path = path;

    public string Path { get; }
    public bool IsHidden { get; init; }
    public bool IsRunning { get; init; }
    public ScriptFlag Flag { get; init; }

    /// <summary>A compact label: <c>app/script</c> for the conventional
    /// <c>&lt;app&gt;/scripts/&lt;name&gt;</c> layout, otherwise the file name.</summary>
    public string DisplayName => ScriptDisplayName.For(Path);
}
