using System;
using System.Globalization;
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
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Stored JSON, indented for reading; text that is not JSON is shown as it is.</summary>
    public static string PrettyJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return JsonSerializer.Serialize(document.RootElement, Indented);
        }
        catch (JsonException)
        {
            return text;
        }
    }

    /// <summary>A run's stored output as its console shows it (<see cref="RunLog.Lines"/>).</summary>
    public static string OutputText(byte[] output) =>
        string.Join("\n", RunLog.Lines(Encoding.UTF8.GetString(output))).TrimEnd('\n');

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
        RecordKind.RunOutput => Localizer.T("records.kindRunOutput"),
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
