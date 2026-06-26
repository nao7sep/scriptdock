using System;

namespace ScriptDock.Models;

/// <summary>
/// A running script recorded in <see cref="AppState"/> so a relaunch can find it again. Identity
/// is the PID <em>plus</em> the OS process start-time: the OS reuses PIDs, but a reused PID has a
/// different start-time, so the pair rules out re-attaching to an unrelated process.
/// </summary>
public sealed class PersistedProcess
{
    /// <summary>OS process id of the launched login shell.</summary>
    public int Pid { get; set; }

    /// <summary>The OS process start time, UTC (ISO-8601, millisecond precision).</summary>
    public DateTimeOffset OsStartedAt { get; set; }

    /// <summary>When ScriptDock launched it, UTC.</summary>
    public DateTimeOffset LaunchedAt { get; set; }

    /// <summary>Absolute path of the launched script.</summary>
    public string ScriptPath { get; set; } = "";

    /// <summary>Path of the per-run log file, so a recaptured run still feeds the console.</summary>
    public string LogFilePath { get; set; } = "";
}
