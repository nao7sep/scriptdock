using System;
using ScriptDock.Backup;
using Xunit;

namespace ScriptDock.Tests.Backup;

/// <summary>The backup index's two time forms: a whole-second ISO stamp that round-trips, and a
/// filename run stamp in the fleet's <c>yyyyMMdd-HHmmss-utc</c> convention.</summary>
public sealed class BackupTimeTests
{
    private static readonly DateTimeOffset Instant =
        new(2026, 7, 1, 2, 22, 20, TimeSpan.Zero);

    [Fact]
    public void ToIsoSeconds_Drops_SubSeconds_And_Marks_Utc()
    {
        var withMillis = Instant.AddMilliseconds(738);

        Assert.Equal("2026-07-01T02:22:20Z", BackupTime.ToIsoSeconds(withMillis));
    }

    [Fact]
    public void ToIsoSeconds_Normalizes_A_NonUtc_Offset_To_Utc()
    {
        var jst = new DateTimeOffset(2026, 7, 1, 11, 22, 20, TimeSpan.FromHours(9));

        Assert.Equal("2026-07-01T02:22:20Z", BackupTime.ToIsoSeconds(jst));
    }

    [Fact]
    public void TryParseIso_RoundTrips_The_Whole_Second_Stamp()
    {
        Assert.True(BackupTime.TryParseIso("2026-07-01T02:22:20Z", out var parsed));
        Assert.Equal(Instant, parsed);
    }

    [Fact]
    public void TryParseIso_Rejects_Garbage()
    {
        Assert.False(BackupTime.TryParseIso("not-a-timestamp", out _));
    }

    [Fact]
    public void FileStamp_Uses_The_Fleet_Convention()
    {
        Assert.Equal("20260701-022220-utc", BackupTime.FileStamp(Instant));
    }
}
