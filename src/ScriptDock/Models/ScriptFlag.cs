namespace ScriptDock.Models;

/// <summary>How a script appears since the last scan, for the list's colour cue.</summary>
public enum ScriptFlag
{
    None,

    /// <summary>Newly found by the most recent scan (shown yellow until the next scan).</summary>
    New,

    /// <summary>Was known but has since disappeared (shown red until the next scan).</summary>
    Removed,
}
