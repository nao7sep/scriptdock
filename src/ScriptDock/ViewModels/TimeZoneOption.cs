using System.Collections.Generic;
using System.Linq;
using ScriptDock.I18n;
using ScriptDock.Services;

namespace ScriptDock.ViewModels;

/// <summary>A line in the Settings time zone list: the value that is saved, and the name shown for it.</summary>
public sealed record TimeZoneOption(string Value, string Name)
{
    /// <summary>System first, named with the zone it resolves to now, then every zone by its IANA name.</summary>
    internal static IReadOnlyList<TimeZoneOption> All() =>
    [
        new(DisplayTime.System, Localizer.T("settings.timeZoneSystem",
            ("zone", DisplayTime.ComputerName()))),
        .. DisplayTime.Zones().Select(zone => new TimeZoneOption(zone, zone)),
    ];

    /// <summary>The line for a saved preference, falling back to System.</summary>
    internal static TimeZoneOption For(string? preference, IReadOnlyList<TimeZoneOption> options) =>
        DisplayTime.TryParse(preference, out var parsed)
            ? options.FirstOrDefault(option => option.Value == parsed) ?? options[0]
            : options[0];
}
