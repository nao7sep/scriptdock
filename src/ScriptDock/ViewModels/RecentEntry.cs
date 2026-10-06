using System;
using ScriptDock.I18n;
using ScriptDock.Models;
using ScriptDock.Services;

namespace ScriptDock.ViewModels;

/// <summary>
/// One row of the Recent list: a script you have launched, keyed by path, merging its
/// persisted last-run time with its live process (if any). A running process makes the entry
/// Running (with output); a finished one keeps its output and exit code until the entry is
/// dismissed; a recent with no live process (carried over from a past session) shows how its last
/// run ended, as recorded.
/// The <see cref="DisplayName"/> is the same disambiguated label the Scripts list uses. Built
/// fresh by <see cref="RecentListBuilder"/> on each refresh.
/// </summary>
public sealed class RecentEntry
{
    public RecentEntry(string path, string displayName, DateTimeOffset lastRanAt, ScriptProcess? process, RecordedEnd? lastEnd = null)
    {
        Path = path;
        DisplayName = displayName;
        LastRanAt = lastRanAt;
        Process = process;
        LastEnd = lastEnd;
    }

    public string Path { get; }
    public string DisplayName { get; }
    public DateTimeOffset LastRanAt { get; }
    public ScriptProcess? Process { get; }

    /// <summary>How the last run ended as recorded, read when there is no live process.</summary>
    public RecordedEnd? LastEnd { get; }

    public bool IsRunning => Kind == PillKind.Running;

    /// <summary>
    /// The last-run time in the computer's own zone (UTC is converted only when facing the user, per the
    /// timestamp conventions), written the way the reader's language and region write a date and a time
    /// to the minute rather than in one fixed English form.
    /// </summary>
    public string LastRanDisplay => Localizer.Current.DateAndMinute(LastRanAt, TimeZoneInfo.Local);

    /// <summary>Short label for the state pill: the live process's state, or with none (a recent
    /// carried over from a past session) the recorded end of its last run, <c>Unknown</c> when no end
    /// was recorded.</summary>
    public string StatePillText => Kind switch
    {
        PillKind.Running => Localizer.T("recent.stateRunning"),
        PillKind.ExitedOk => Localizer.T("recent.stateExited"),
        PillKind.ExitedError => Localizer.T("recent.stateExitedWithCode", ("code", ExitCode)),
        PillKind.Stopped => Localizer.T("recent.stateStopped"),
        PillKind.Failed => Localizer.T("recent.stateFailed"),
        _ => Localizer.T("recent.stateUnknown"),
    };

    private int? ExitCode => Process is { } process ? process.ExitCode : LastEnd?.ExitCode;

    /// <summary>Whether the state pill reads as a failure. With <see cref="IsRunning"/> it is the
    /// pill's colour state, which the view maps to theme brushes: green when running, red on a
    /// failure or non-zero exit, muted gray for done/unknown. Distinguishes states at a glance
    /// without relying on the text alone.</summary>
    public bool IsPillFailed => Kind is PillKind.Failed or PillKind.ExitedError;

    // The run's display lifecycle, derived once so the pill text and brush can't disagree, and so
    // the "no live process reads the recorded end" and "Exited 0/null vs non-zero" rules live in one
    // place rather than being re-derived at each call site.
    private enum PillKind { Unknown, Running, ExitedOk, ExitedError, Stopped, Failed }

    private PillKind Kind => Process switch
    {
        null => RecordedKind(LastEnd),
        { State: RunState.Running } => PillKind.Running,
        { State: RunState.Exited, ExitCode: 0 or null } => PillKind.ExitedOk,
        { State: RunState.Exited } => PillKind.ExitedError,
        { State: RunState.Terminated } => PillKind.Stopped,
        _ => PillKind.Unknown,
    };

    // The records' own state values. No recorded end (ScriptDock crashed, or a tree outlived the quit
    // bound) says nothing about how the run ended, and neither does any other value.
    private static PillKind RecordedKind(RecordedEnd? end) => end?.State switch
    {
        "exited" => end.ExitCode is 0 or null ? PillKind.ExitedOk : PillKind.ExitedError,
        "terminated" => PillKind.Stopped,
        "failed" => PillKind.Failed,
        _ => PillKind.Unknown,
    };
}
