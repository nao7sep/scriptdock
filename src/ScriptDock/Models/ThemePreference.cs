using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScriptDock.Models;

/// <summary>The saved appearance choice (app-chrome conventions, Theme). System follows the OS.</summary>
public enum ThemePreference
{
    System,
    Light,
    Dark,
}

/// <summary>
/// Writes the theme as a lowercase name and reads any name case-insensitively. A missing,
/// unrecognized, or wrongly typed value reads as System rather than failing the whole
/// config.json, so a file from before the setting, or from a newer build, loads untouched.
/// Applied on the property: the store's general enum converter would reject an unknown name.
/// </summary>
public sealed class ThemePreferenceJsonConverter : JsonConverter<ThemePreference>
{
    public override ThemePreference Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String
            && reader.GetString() is { } name
            && !int.TryParse(name, out _)
            && Enum.TryParse<ThemePreference>(name, ignoreCase: true, out var value)
            && Enum.IsDefined(value))
        {
            return value;
        }

        reader.Skip();
        return ThemePreference.System;
    }

    public override void Write(Utf8JsonWriter writer, ThemePreference value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString().ToLowerInvariant());
}
