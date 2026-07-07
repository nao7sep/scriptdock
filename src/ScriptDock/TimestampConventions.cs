using System;
using System.Globalization;

namespace ScriptDock;

/// <summary>
/// The single source of truth for ScriptDock's timestamp shapes, per the timestamp
/// conventions. All three render UTC and are culture-invariant, so none depends on the
/// machine's locale or time zone:
/// <list type="bullet">
/// <item><see cref="IsoMillis"/> — the ISO-8601 UTC form with three fractional digits and a
/// <c>Z</c> suffix (<c>2026-06-17T00:15:41.123Z</c>), used inside stored data (a backup row's
/// <c>written_at_utc</c>) and log envelopes.</item>
/// <item><see cref="FileStamp"/> — the whole-second compact token used in log <em>file
/// names</em> (<c>20260617-001541-utc</c>); callers prefix/suffix it (run:
/// <c>{stamp}-{name}-{id}.log</c>).</item>
/// <item><see cref="FileStampMillis"/> — the same compact token with millisecond precision
/// (<c>20260617-001541-123-utc</c>), used where two events in the same UTC second must still
/// be distinguishable: the session-log filename, a quarantine name, and the scan-report log
/// filename (<c>scan-{stamp}.log</c>).</item>
/// </list>
/// Keeping every form here means a convention change is a one-line edit that every writer
/// inherits, rather than a hunt across the logger, the converter, and the log/archive namers.
/// </summary>
public static class TimestampConventions
{
    /// <summary>The ISO-8601 UTC-millis format string (with literal <c>T</c> and <c>Z</c>).</summary>
    public const string Iso8601Millis = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    private const string FileStampFormat = "yyyyMMdd-HHmmss";
    private const string FileStampMillisFormat = "yyyyMMdd-HHmmss-fff";

    /// <summary>Formats an instant as ISO-8601 UTC with millisecond precision and a <c>Z</c> suffix.</summary>
    public static string IsoMillis(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString(Iso8601Millis, CultureInfo.InvariantCulture);

    /// <summary>The filename time token for log artifacts: the UTC instant as
    /// <c>yyyyMMdd-HHmmss-utc</c>. The <c>-utc</c> marker records that the time is UTC.</summary>
    public static string FileStamp(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString(FileStampFormat, CultureInfo.InvariantCulture) + "-utc";

    /// <summary>The millisecond-precision filename time token: the UTC instant as
    /// <c>yyyyMMdd-HHmmss-fff-utc</c>. Used where whole-second precision is not enough to keep
    /// two names apart (the session log, a quarantine name).</summary>
    public static string FileStampMillis(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString(FileStampMillisFormat, CultureInfo.InvariantCulture) + "-utc";
}
