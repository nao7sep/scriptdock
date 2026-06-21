using System;
using System.Globalization;

namespace ScriptDock;

/// <summary>
/// The single source of truth for ScriptDock's two timestamp shapes, per the timestamp
/// conventions. Both render UTC and are culture-invariant, so neither depends on the machine's
/// locale or time zone:
/// <list type="bullet">
/// <item><see cref="IsoMillis"/> — the ISO-8601 UTC form with three fractional digits and a
/// <c>Z</c> suffix (<c>2026-06-17T00:15:41.123Z</c>), used inside stored data and log
/// envelopes.</item>
/// <item><see cref="FileStamp"/> — the compact token used in log <em>file names</em>
/// (<c>20260617-001541-utc</c>); callers prefix/suffix it (session: <c>{stamp}.log</c>; scan:
/// <c>scan-{stamp}.log</c>; run: <c>{stamp}-{name}-{id}.log</c>).</item>
/// </list>
/// Keeping both forms here means a convention change is a one-line edit that every writer
/// inherits, rather than a hunt across the logger, the converter, and three log-file namers.
/// </summary>
public static class TimestampConventions
{
    /// <summary>The ISO-8601 UTC-millis format string (with literal <c>T</c> and <c>Z</c>).</summary>
    public const string Iso8601Millis = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    private const string FileStampFormat = "yyyyMMdd-HHmmss";

    /// <summary>Formats an instant as ISO-8601 UTC with millisecond precision and a <c>Z</c> suffix.</summary>
    public static string IsoMillis(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString(Iso8601Millis, CultureInfo.InvariantCulture);

    /// <summary>The filename time token for log artifacts: the UTC instant as
    /// <c>yyyyMMdd-HHmmss-utc</c>. The <c>-utc</c> marker records that the time is UTC.</summary>
    public static string FileStamp(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString(FileStampFormat, CultureInfo.InvariantCulture) + "-utc";
}
