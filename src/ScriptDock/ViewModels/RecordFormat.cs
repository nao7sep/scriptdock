using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ScriptDock.I18n;
using ScriptDock.Models;
using ScriptDock.Services;

namespace ScriptDock.ViewModels;

/// <summary>How the Records window shows what a record holds: its stored values, made readable but not changed.</summary>
public static class RecordFormat
{
    private static readonly JsonWriterOptions Indented = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // A log line's envelope, which the detail pane already shows as its time, level and title.
    private static readonly string[] LogEnvelope = ["time", "level", "message"];

    // Each block below is null when it holds nothing to show, and the pane then leaves it out.

    /// <summary>Stored JSON, indented for reading; text that is not JSON is shown as it is. Null when it is
    /// empty: blank, or JSON <c>null</c>, <c>{}</c>, <c>[]</c> or a blank string.</summary>
    public static string? PrettyJson(string? text) => Pretty(text, []);

    /// <summary>A stored log line's own fields, as <see cref="PrettyJson"/> shows them, without the envelope
    /// the pane shows above them; null when none remain.</summary>
    public static string? LogDetails(string line) => Pretty(line, LogEnvelope);

    /// <summary>The most of a run's stored output the detail pane renders, so a very large output does not
    /// stall it. The stored output is never cut, and search reads all of it.</summary>
    public const int OutputShownBytes = 1024 * 1024;

    /// <summary>A run's stored output as its console shows it (<see cref="RunLog.Lines"/>), at most its first
    /// <paramref name="limit"/> bytes, cut at a character boundary, with how many stored bytes it leaves
    /// out; null when there is none or all of it is only whitespace.</summary>
    public static OutputPreview? Output(byte[]? output, int limit = OutputShownBytes)
    {
        if (output is null)
            return null;

        var cut = Math.Min(output.Length, limit);
        // Back off to the start of a UTF-8 sequence, so the cut never splits a character.
        while (cut > 0 && cut < output.Length && (output[cut] & 0xC0) == 0x80)
            cut--;

        var text = string.Join("\n", RunLog.Lines(Encoding.UTF8.GetString(output, 0, cut))).TrimEnd('\n');
        var more = output.Length - cut;
        return string.IsNullOrWhiteSpace(text) && more == 0 ? null : new OutputPreview(text, more);
    }

    private static string? Pretty(string? text, string[] omit)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var fields = root.ValueKind == JsonValueKind.Object
                ? root.EnumerateObject().Where(field => !omit.Contains(field.Name)).ToList()
                : null;
            var empty = root.ValueKind switch
            {
                JsonValueKind.Object => fields!.Count == 0,
                JsonValueKind.Array => root.GetArrayLength() == 0,
                JsonValueKind.String => string.IsNullOrWhiteSpace(root.GetString()),
                JsonValueKind.Null => true,
                _ => false,
            };
            if (empty)
                return null;

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, Indented))
            {
                if (fields is null)
                {
                    root.WriteTo(writer);
                }
                else
                {
                    writer.WriteStartObject();
                    foreach (var field in fields)
                        field.WriteTo(writer);
                    writer.WriteEndObject();
                }
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (JsonException)
        {
            return text;
        }
    }

    /// <summary>A stored time in the computer's own zone, to the second or the millisecond; text that is not
    /// a time is shown as it is.</summary>
    public static string Time(string stored, bool milliseconds = false) =>
        DateTimeOffset.TryParse(stored, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment)
            ? Localizer.Current.DateAndSecond(moment, TimeZoneInfo.Local, milliseconds)
            : stored;

    /// <summary>A launch, named by its session's start time, marked when it is this one.</summary>
    public static string Launch(string session, string currentSession)
    {
        var time = Time(session);
        return session == currentSession ? Localizer.T("records.thisLaunch", ("time", time)) : time;
    }

    public static string KindLabel(RecordKind kind) => kind switch
    {
        RecordKind.Log => Localizer.T("records.kindLog"),
        RecordKind.Run => Localizer.T("records.kindRun"),
        _ => Localizer.T("records.kindScanReport"),
    };

    public static string LevelLabel(LogLevel level) => level switch
    {
        LogLevel.Error => Localizer.T("records.levelError"),
        LogLevel.Warn => Localizer.T("records.levelWarn"),
        LogLevel.Debug => Localizer.T("records.levelDebug"),
        _ => Localizer.T("records.levelInfo"),
    };

    public static string LevelFilterLabel(RecordLevelFilter level) => level switch
    {
        RecordLevelFilter.Attention => Localizer.T("records.levelAttention"),
        RecordLevelFilter.Error => Localizer.T("records.levelError"),
        RecordLevelFilter.Warn => Localizer.T("records.levelWarn"),
        RecordLevelFilter.Debug => Localizer.T("records.levelDebug"),
        _ => Localizer.T("records.levelInfo"),
    };

    /// <summary>A scan report has no title of its own; it is named by what it found.</summary>
    public static string ScanTitle(int? found) => Localizer.T("records.scanFound", ("count", found ?? 0));
}

/// <summary>The part of a run's output the detail pane shows, and how many stored bytes follow it.</summary>
public sealed record OutputPreview(string Text, long MoreBytes);
