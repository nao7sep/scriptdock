namespace ScriptDock.Models;

/// <summary>
/// A script as shown in the Scripts list: its path, the disambiguated <see cref="DisplayName"/>
/// (assigned by the builder from <see cref="ScriptLabels"/>), whether it is hidden, whether it
/// is currently running (for the tile's running dot), and its since-last-scan flag (new/removed)
/// for the colour cue. Immutable once built.
/// </summary>
public sealed class ScriptItem
{
    public ScriptItem(string path) => Path = path;

    public string Path { get; }
    public string DisplayName { get; init; } = "";
    public bool IsHidden { get; init; }
    public bool IsRunning { get; init; }
    public ScriptFlag Flag { get; init; }

    /// <summary>True when the script was newly found in the latest scan — flagged by an accent dot.</summary>
    public bool IsNew => Flag == ScriptFlag.New;
}
