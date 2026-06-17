using System;
using System.Globalization;
using ScriptDock.Models;
using ScriptDock.Services;

namespace ScriptDock.ViewModels;

/// <summary>
/// One row of the Recent list: a script you have launched, keyed by path, merging its
/// persisted last-run time with its live process (if any). A running process makes the entry
/// Running (with output); a finished one keeps its output and exit code until the entry is
/// dismissed; a recent with no live process (carried over from a past session) shows idle.
/// The <see cref="DisplayName"/> is the same disambiguated label the Scripts list uses. Built
/// fresh by <see cref="DockListBuilder"/> on each refresh.
/// </summary>
public sealed class DockEntry
{
    private static readonly TimeZoneInfo DisplayZone = ResolveDisplayZone();

    public DockEntry(string path, string displayName, DateTimeOffset lastRanAt, ScriptProcess? process)
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

    /// <summary>Compact status for the row: <c>running</c>, <c>exited (0)</c>, etc., or
    /// <c>—</c> when there is no live process (a recent carried over from a past session).</summary>
    public string StateText => Process is null
        ? "—"
        : Process.State switch
        {
            RunState.Running => "running",
            RunState.Exited => $"exited ({Process.ExitCode})",
            RunState.Terminated => "terminated",
            RunState.Failed => "failed",
            _ => Process.State.ToString().ToLowerInvariant(),
        };

    private static TimeZoneInfo ResolveDisplayZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Local; }
    }
}
