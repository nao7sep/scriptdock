using System;
using System.Globalization;
using Avalonia.Media;
using ScriptDock.Models;
using ScriptDock.Services;

namespace ScriptDock.ViewModels;

/// <summary>
/// One row of the Recent list: a script you have launched, keyed by path, merging its
/// persisted last-run time with its live process (if any). A running process makes the entry
/// Running (with output); a finished one keeps its output and exit code until the entry is
/// dismissed; a recent with no live process (carried over from a past session) shows idle.
/// The <see cref="DisplayName"/> is the same disambiguated label the Scripts list uses. Built
/// fresh by <see cref="RecentListBuilder"/> on each refresh.
/// </summary>
public sealed class RecentEntry
{
    private static readonly TimeZoneInfo DisplayZone = ResolveDisplayZone();

    public RecentEntry(string path, string displayName, DateTimeOffset lastRanAt, ScriptProcess? process)
    {
        Path = path;
        DisplayName = displayName;
        LastRanAt = lastRanAt;
        Process = process;
    }

    public string Path { get; }
    public string DisplayName { get; }
    public DateTimeOffset LastRanAt { get; }
    public ScriptProcess? Process { get; }

    public bool IsRunning => Kind == PillKind.Running;

    /// <summary>The last-run time in local (JST) display form.</summary>
    public string LastRanDisplay =>
        TimeZoneInfo.ConvertTime(LastRanAt, DisplayZone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Short label for the state pill. No live process (a recent carried over from a
    /// past session) reads <c>Idle</c> rather than a bare dash.</summary>
    public string StatePillText => Kind switch
    {
        PillKind.Running => "Running",
        PillKind.ExitedOk => "Exited",
        PillKind.ExitedError => $"Exited {Process!.ExitCode}",
        PillKind.Stopped => "Stopped",
        PillKind.Failed => "Failed",
        _ => "Idle",
    };

    /// <summary>Background brush for the state pill, resolved from the shared palette: green when
    /// running, red on a failure or non-zero exit, muted gray for done/idle. Distinguishes states
    /// at a glance without relying on the text alone.</summary>
    public IBrush StatePillBrush => Palette.Brush(Kind switch
    {
        PillKind.Running => "RunningBrush",
        PillKind.Failed or PillKind.ExitedError => "DangerTextBrush",
        _ => "TextSecondaryBrush",
    });

    // The run's display lifecycle, derived once so the pill text and brush can't disagree, and so
    // the "no live process means Idle" and "Exited 0/null vs non-zero" rules live in one place
    // rather than being re-derived at each call site.
    private enum PillKind { Idle, Running, ExitedOk, ExitedError, Stopped, Failed }

    private PillKind Kind => Process switch
    {
        null => PillKind.Idle,
        { State: RunState.Running } => PillKind.Running,
        { State: RunState.Exited, ExitCode: 0 or null } => PillKind.ExitedOk,
        { State: RunState.Exited } => PillKind.ExitedError,
        { State: RunState.Terminated } => PillKind.Stopped,
        { State: RunState.Failed } => PillKind.Failed,
        _ => PillKind.Idle,
    };

    private static TimeZoneInfo ResolveDisplayZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Local; }
    }
}
