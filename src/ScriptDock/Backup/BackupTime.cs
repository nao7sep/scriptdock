using System;
using System.Globalization;

namespace ScriptDock.Backup;

/// <summary>
/// The two time forms the backup index needs, kept beside the pure backup logic so the change decision
/// is unit-testable without touching the app's wider timestamp machinery. The run stamp reuses the
/// fleet-wide <c>yyyyMMdd-HHmmss-utc</c> file-stamp convention (see <see cref="TimestampConventions"/>);
/// the stored modification time is a whole-second ISO-8601 UTC value, since it is compared with a
/// two-second tolerance and so carries no sub-second component.
/// </summary>
public static class BackupTime
{
    /// <summary>The filename-safe UTC run stamp, <c>yyyyMMdd-HHmmss-utc</c> — the archive's stem and each
    /// index entry's <c>archivedAt</c>.</summary>
    public static string FileStamp(DateTimeOffset value) => TimestampConventions.FileStamp(value);

    /// <summary>A whole-second UTC ISO-8601 stamp (<c>yyyy-MM-ddTHH:mm:ssZ</c>) for the index's stored
    /// modification time. Parses back through <see cref="TryParseIso"/>.</summary>
    public static string ToIsoSeconds(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Attempts to parse a stored ISO-8601 modification time. Returns false instead of throwing,
    /// so a hand-mangled index entry forces a recapture rather than failing the whole run.</summary>
    public static bool TryParseIso(string text, out DateTimeOffset value)
    {
        const DateTimeStyles styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

        string[] formats =
        {
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            "yyyy-MM-dd'T'HH:mm:sszzz",
            "yyyy-MM-dd'T'HH:mm:ss.fffzzz",
        };

        return DateTimeOffset.TryParseExact(text, formats, CultureInfo.InvariantCulture, styles, out value)
            || DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, styles, out value);
    }

    /// <summary>Truncates a UTC modification time to the whole second, matching what the index stores.</summary>
    public static DateTimeOffset ToWholeSecondUtc(DateTime lastWriteUtc)
    {
        var utc = DateTime.SpecifyKind(lastWriteUtc, DateTimeKind.Utc);
        return new DateTimeOffset(utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerSecond)), TimeSpan.Zero);
    }
}
