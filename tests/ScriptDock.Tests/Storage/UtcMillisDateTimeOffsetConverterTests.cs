using System;
using System.Text.Json;
using ScriptDock.Storage;
using Xunit;

namespace ScriptDock.Tests.Storage;

/// <summary>
/// Locks the on-disk timestamp form required by the timestamp-conventions: ISO-8601 UTC,
/// exactly three fractional digits, <c>Z</c> suffix. A drift here would silently change
/// the shape of every persisted timestamp.
/// </summary>
public sealed class UtcMillisDateTimeOffsetConverterTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new UtcMillisDateTimeOffsetConverter() },
    };

    [Fact]
    public void Write_EmitsThreeFractionalDigitsAndZ()
    {
        var value = new DateTimeOffset(2026, 6, 17, 0, 15, 41, 123, TimeSpan.Zero);

        var json = JsonSerializer.Serialize(value, Options);

        Assert.Equal("\"2026-06-17T00:15:41.123Z\"", json);
    }

    [Fact]
    public void Write_TruncatesSubMillisecondPrecision()
    {
        // 123.4567 ms past the second; the format keeps three digits, no rounding artifacts.
        var value = new DateTimeOffset(2026, 6, 17, 0, 15, 41, TimeSpan.Zero).AddTicks(1_234_567);

        var json = JsonSerializer.Serialize(value, Options);

        Assert.Equal("\"2026-06-17T00:15:41.123Z\"", json);
    }

    [Fact]
    public void Write_ConvertsNonUtcOffsetToUtc()
    {
        // 09:15:41.123 at +09:00 (JST) is 00:15:41.123Z.
        var value = new DateTimeOffset(2026, 6, 17, 9, 15, 41, 123, TimeSpan.FromHours(9));

        var json = JsonSerializer.Serialize(value, Options);

        Assert.Equal("\"2026-06-17T00:15:41.123Z\"", json);
    }

    [Fact]
    public void Read_NormalisesOffsetInputToUtc()
    {
        var value = JsonSerializer.Deserialize<DateTimeOffset>("\"2026-06-17T09:15:41.123+09:00\"", Options);

        Assert.Equal(TimeSpan.Zero, value.Offset);
        Assert.Equal(new DateTimeOffset(2026, 6, 17, 0, 15, 41, 123, TimeSpan.Zero), value);
    }

    [Fact]
    public void RoundTrips()
    {
        var value = new DateTimeOffset(2026, 1, 2, 3, 4, 5, 678, TimeSpan.Zero);

        var back = JsonSerializer.Deserialize<DateTimeOffset>(JsonSerializer.Serialize(value, Options), Options);

        Assert.Equal(value, back);
    }
}
