using System;
using ScriptDock.Services;

namespace ScriptDock.Models;

/// <summary>
/// The records the Records window lists: a log line, a run, and a scan report. A run is listed from its
/// start, with its latest recorded end and its imported output once there are any; the log lines tell of
/// starts, stops and Recent dismissals as they happened.
/// </summary>
public enum RecordKind
{
    Log,
    Run,
    ScanReport,
}

/// <summary>What the level filter offers: a record's own level, or every record that needs attention.</summary>
public enum RecordLevelFilter
{
    /// <summary>Every record at <c>warn</c> or <c>error</c>, a failed run's output included.</summary>
    Attention,
    Error,
    Warn,
    Info,
    Debug,
}

public static class RecordKinds
{
    /// <summary>The name a kind has in the records' queries; the list orders kinds by it.</summary>
    public static string Name(RecordKind kind) => kind switch
    {
        RecordKind.Log => "log",
        RecordKind.Run => "run",
        RecordKind.ScanReport => "scan-report",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static RecordKind Parse(string name) => name switch
    {
        "log" => RecordKind.Log,
        "run" => RecordKind.Run,
        "scan-report" => RecordKind.ScanReport,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    /// <summary>A stored level; the records only ever hold the four the logger writes.</summary>
    public static LogLevel Level(string name) => name switch
    {
        "debug" => LogLevel.Debug,
        "warn" => LogLevel.Warn,
        "error" => LogLevel.Error,
        _ => LogLevel.Info,
    };
}

/// <summary>Where the next page starts: the last summary of the page before it.</summary>
public sealed record RecordCursor(string Time, RecordKind Kind, long Id);

/// <summary>A page of records to read: the filters, all off when null or empty, and where it starts.</summary>
public sealed record RecordsQuery(
    string? Session,
    RecordKind? Kind,
    RecordLevelFilter? Level,
    string Search,
    RecordCursor? After)
{
    public static readonly RecordsQuery All = new(null, null, null, string.Empty, null);
}

/// <summary>
/// One row of the list. <paramref name="Time"/> is the stored ISO time. <paramref name="Title"/> is a log
/// line's message or a run's script; <paramref name="Text"/> is a scan's roots; <paramref name="Found"/>
/// is how many scripts a scan found. A run's time is its start; it reads as <c>error</c> when it failed or
/// exited with another code than 0, and a scan report as <c>info</c>.
/// </summary>
public sealed record RecordSummary(
    RecordKind Kind,
    long Id,
    string Session,
    string Time,
    LogLevel Level,
    string Title,
    string? Text,
    int? Found)
{
    public string Key => $"{RecordKinds.Name(Kind)}:{Id}";
}

public sealed record RecordsPage(System.Collections.Generic.IReadOnlyList<RecordSummary> Records, bool More);

/// <summary>One record whole, every field as stored. Times are the stored ISO text.</summary>
public abstract record RecordDetail(RecordKind Kind, long Id, string Session, string Time, LogLevel Level);

/// <summary>A log line: its envelope and the whole event as the logger wrote it.</summary>
public sealed record LogRecordDetail(long Id, string Session, string Time, LogLevel Level, string Message, string Line)
    : RecordDetail(RecordKind.Log, Id, Session, Time, Level);

/// <summary>
/// A run with its latest recorded end and its imported output, each null until there is one. <c>Time</c>
/// is when the run started; <paramref name="ImportedAt"/> is when its output was imported.
/// </summary>
public sealed record RunRecordDetail(
    long Id,
    string Session,
    string Time,
    LogLevel Level,
    int Run,
    string Script,
    int? Pid,
    string? OsStartedAt,
    string? OutputPath,
    string? EndedAt,
    string? EndState,
    int? ExitCode,
    string? ImportedAt,
    byte[]? Output)
    : RecordDetail(RecordKind.Run, Id, Session, Time, Level)
{
    /// <summary>Has both its end and its output, so nothing more will be recorded for it.</summary>
    public bool Settled => EndState is not null && Output is not null;

    /// <summary>The same recorded end and import as <paramref name="other"/>, the only parts a run gains later.</summary>
    public bool SameProgress(RunRecordDetail other) =>
        (EndedAt, EndState, ExitCode, ImportedAt, Level) == (other.EndedAt, other.EndState, other.ExitCode, other.ImportedAt, other.Level);
}

/// <summary>A scan report: when the scan completed and the whole report as stored.</summary>
public sealed record ScanReportRecordDetail(long Id, string Session, string Time, int? Found, string Report)
    : RecordDetail(RecordKind.ScanReport, Id, Session, Time, LogLevel.Info);

/// <summary>The launches the filter offers, newest first, and this one.</summary>
public sealed record RecordSources(string CurrentSession, System.Collections.Generic.IReadOnlyList<string> Sessions);
