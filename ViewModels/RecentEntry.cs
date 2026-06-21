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

    public bool IsRunning => Process is { State: RunState.Running };

    /// <summary>The last-run time in local (JST) display form.</summary>
    public string LastRanDisplay =>
        TimeZoneInfo.ConvertTime(LastRanAt, DisplayZone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Short label for the state pill. No live process (a recent carried over from a
    /// past session) reads <c>Idle</c> rather than a bare dash.</summary>
    public string StatePillText => Process is null
        ? "Idle"
        : Process.State switch
        {
            RunState.Running => "Running",
            RunState.Exited => Process.ExitCode is 0 or null ? "Exited" : $"Exited {Process.ExitCode}",
            RunState.Terminated => "Stopped",
            RunState.Failed => "Failed",
            _ => Process.State.ToString(),
        };

    /// <summary>Background brush for the state pill, resolved from the shared palette: green when
    /// running, red on a failure or non-zero exit, muted gray for done/idle. Distinguishes states
    /// at a glance without relying on the text alone.</summary>
    public IBrush StatePillBrush => Palette.Brush(
        Process is null ? "TextSecondaryBrush"
        : Process.State switch
        {
            RunState.Running => "RunningBrush",
            RunState.Failed => "DangerTextBrush",
            RunState.Exited when Process.ExitCode is not (0 or null) => "DangerTextBrush",
            _ => "TextSecondaryBrush",
        });

    private static TimeZoneInfo ResolveDisplayZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Local; }
    }
}
