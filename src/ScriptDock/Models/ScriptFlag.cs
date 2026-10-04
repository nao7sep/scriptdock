namespace ScriptDock.Models;

/// <summary>How a script appears since the last scan, for the list's colour cue.</summary>
public enum ScriptFlag
{
    None,

    /// <summary>Newly found by the most recent scan (shown orange until the script runs, the next full scan, or a
    /// scan-settings change; see ScanFlags).</summary>
    New,

    /// <summary>Was known but has since disappeared (shown red until the next full scan).</summary>
    Removed,
}
