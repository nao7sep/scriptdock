using System;
using System.IO;
using ScriptDock.Storage;

namespace ScriptDock.Services;

/// <summary>Where a <see cref="SessionLogger"/> puts each serialized event.</summary>
public interface ILogSink : IDisposable
{
    /// <summary>Takes one event: <paramref name="line"/> is the whole JSON object, the other values its envelope.</summary>
    void Write(LogLevel level, string time, string message, string line);

    void Flush();
}

/// <summary>
/// Writes each event as one line to a <see cref="TextWriter"/> — the console before the records are open and
/// after they close. <c>info</c> may stay buffered; every other level is flushed at once (logging-conventions).
/// </summary>
public sealed class TextWriterLogSink : ILogSink
{
    private readonly TextWriter _writer;
    private readonly bool _leaveOpen;

    public TextWriterLogSink(TextWriter writer, bool leaveOpen)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _leaveOpen = leaveOpen;
    }

    public void Write(LogLevel level, string time, string message, string line)
    {
        _writer.WriteLine(line);
        if (level != LogLevel.Info)
            _writer.Flush();
    }

    public void Flush()
    {
        try { _writer.Flush(); }
        catch (Exception ex)
        {
            SessionLogger.EmitToConsole($"[logger] flush failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        try { _writer.Flush(); }
        catch (Exception ex)
        {
            SessionLogger.EmitToConsole($"[logger] final flush failed: {ex.GetType().Name}: {ex.Message}");
        }

        if (!_leaveOpen)
        {
            try { _writer.Dispose(); }
            catch (Exception ex)
            {
                SessionLogger.EmitToConsole($"[logger] dispose failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}

/// <summary>Writes each event as a log record in the <see cref="RecordStore"/> (logging-conventions).</summary>
public sealed class RecordLogSink : ILogSink
{
    private readonly RecordStore _records;

    public RecordLogSink(RecordStore records) => _records = records;

    public void Write(LogLevel level, string time, string message, string line) =>
        _records.AddLog(time, SessionLogger.LevelName(level), message, line);

    public void Flush() => _records.Flush();

    public void Dispose() => _records.Flush();
}
