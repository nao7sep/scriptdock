using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScriptDock.Storage;

/// <summary>
/// Serialises <see cref="DateTimeOffset"/> as ISO-8601 UTC with exactly three fractional
/// digits and a <c>Z</c> suffix — e.g. <c>2026-06-17T00:15:41.123Z</c> — which is the
/// internal-storage form the timestamp-conventions require. Non-UTC values are converted
/// to UTC before writing; on read, any parseable ISO-8601 value (offset or <c>Z</c>) is
/// normalised to UTC so comparisons and newest-first sorts operate on a single timeline.
/// </summary>
public sealed class UtcMillisDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        if (text is null)
            throw new JsonException("Expected an ISO-8601 timestamp string.");

        return DateTimeOffset.Parse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture));
    }
}
