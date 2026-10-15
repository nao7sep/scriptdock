using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ScriptDock.Services;

/// <summary>
/// The zone every user-facing time is shown in (timestamp-conventions, Time zones): System, the default,
/// follows the computer's zone on each launch and falls back to UTC when that is unavailable; otherwise an
/// IANA zone the user chose from the platform's list. ScriptDock reads no times without a zone, so it has
/// no interpretation zone. Times are stored in UTC and converted only where they are shown.
/// </summary>
public static class DisplayTime
{
    /// <summary>The stored value that follows the computer.</summary>
    public const string System = "system";

    private static IReadOnlyList<string>? s_zones;

    /// <summary>The zone times are shown in now.</summary>
    public static TimeZoneInfo Zone { get; private set; } = Resolve(System);

    /// <summary>Raised on the UI thread when <see cref="Zone"/> changes, so shown times are rendered again.</summary>
    public static event Action? Changed;

    /// <summary>Shows times in <paramref name="preference"/>'s zone from now on.</summary>
    public static void Use(string? preference)
    {
        var zone = Resolve(preference);
        if (zone.Equals(Zone) && zone.HasSameRules(Zone))
            return;
        Zone = zone;
        Changed?.Invoke();
    }

    /// <summary>The platform's IANA zones, by name. On Windows each system zone maps to its IANA name.</summary>
    public static IReadOnlyList<string> Zones() => s_zones ??= [.. TimeZoneInfo.GetSystemTimeZones()
        .Select(zone => IanaName(zone.Id))
        .OfType<string>()
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)];

    /// <summary>A stored value the app can act on: <see cref="System"/> or a zone the platform knows.</summary>
    public static bool TryParse(string? saved, out string preference)
    {
        preference = System;
        if (string.Equals(saved, System, StringComparison.Ordinal))
            return true;
        if (saved is null || Find(saved) is null)
            return false;
        preference = saved;
        return true;
    }

    /// <summary>The zone <paramref name="preference"/> resolves to; the computer's for System or a zone
    /// the platform does not know, and UTC when the computer's is unavailable.</summary>
    public static TimeZoneInfo Resolve(string? preference) =>
        (preference is null || preference == System ? null : Find(preference)) ?? Computer();

    /// <summary>A zone's IANA name, as the list shows it.</summary>
    public static string NameOf(TimeZoneInfo zone) => IanaName(zone.Id) ?? zone.Id;

    /// <summary>
    /// The computer's zone by its canonical IANA name, as the platform reports it, for the System entry.
    /// Off Windows that is the <c>/etc/localtime</c> link's target after <c>zoneinfo/</c>, as macOS's own
    /// time-zone API reads it: .NET names the local zone by matching zone data when the link points outside
    /// <c>/usr/share/zoneinfo</c>, and can then return a legacy alias such as "Japan". Windows names map to IANA.
    /// </summary>
    public static string ComputerName() =>
        (OperatingSystem.IsWindows() ? null : NameFromLink(LocalTimeLink())) ?? NameOf(Computer());

    /// <summary>The IANA name in a zone file's path, after its last <c>zoneinfo/</c>; null when there is none.</summary>
    internal static string? NameFromLink(string? target)
    {
        const string Marker = "zoneinfo/";
        var at = target?.LastIndexOf(Marker, StringComparison.Ordinal) ?? -1;
        var name = at < 0 ? null : target![(at + Marker.Length)..];
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private static string? LocalTimeLink()
    {
        try
        {
            return new FileInfo("/etc/localtime").LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static TimeZoneInfo Computer()
    {
        try
        {
            return TimeZoneInfo.Local;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    private static TimeZoneInfo? Find(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    // Off Windows the platform's ids already are IANA names; Windows ids map to theirs.
    private static string? IanaName(string id) =>
        !OperatingSystem.IsWindows() ? id
        : TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana) ? iana
        : null;
}
