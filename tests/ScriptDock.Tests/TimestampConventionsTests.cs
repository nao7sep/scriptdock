using System;
using ScriptDock;
using Xunit;

namespace ScriptDock.Tests;

/// <summary>
/// Locks the two shared timestamp shapes: both render in UTC regardless of the input's offset, so
/// every log writer and the storage converter agree on one timeline and one filename token.
/// </summary>
public sealed class TimestampConventionsTests
{
    // 2026-06-17 09:15:41.123 +09:00 (JST) == 2026-06-17 00:15:41.123 UTC.
    private static readonly DateTimeOffset Jst =
        new(2026, 6, 17, 9, 15, 41, 123, TimeSpan.FromHours(9));

    [Fact]
    public void IsoMillis_RendersUtcWithMillisAndZ()
    {
        Assert.Equal("2026-06-17T00:15:41.123Z", TimestampConventions.IsoMillis(Jst));
    }

    [Fact]
    public void IsoMillis_OfAlreadyUtc_IsUnchanged()
    {
        var utc = new DateTimeOffset(2026, 6, 17, 0, 15, 41, 123, TimeSpan.Zero);
        Assert.Equal("2026-06-17T00:15:41.123Z", TimestampConventions.IsoMillis(utc));
    }

    [Fact]
    public void FileStamp_RendersCompactUtcTokenWithUtcMarker()
    {
        Assert.Equal("20260617-001541-utc", TimestampConventions.FileStamp(Jst));
    }
}
