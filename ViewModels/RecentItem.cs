using System;
using System.Globalization;
using ScriptDock.Models;

namespace ScriptDock.ViewModels;

/// <summary>
/// Bindable row for the recently-run list. Stores the run time as UTC and displays it in
/// local time (JST per the defaults / timestamp-conventions).
/// </summary>
public sealed class RecentItem
{
    private static readonly TimeZoneInfo DisplayZone = ResolveDisplayZone();

    public RecentItem(RecentRun run)
    {
        Path = run.Path;
        RanAt = run.RanAt;
    }

    public string Path { get; }
    public DateTimeOffset RanAt { get; }
    public string DisplayName => ScriptDisplayName.For(Path);

    public string RanAtDisplay =>
        TimeZoneInfo.ConvertTime(RanAt, DisplayZone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static TimeZoneInfo ResolveDisplayZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Local;
        }
    }
}
