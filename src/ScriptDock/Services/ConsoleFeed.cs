using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace ScriptDock.Services;

/// <summary>What a run's log gained since the last read, as console lines.</summary>
/// <param name="Reset">The console starts over with these lines: the first read, or a jump past more than it shows.</param>
/// <param name="Completed">Lines that ended since the last read, in order.</param>
/// <param name="Open">The line still arriving, as it shows now; empty when the log ends with a line end.</param>
public sealed record ConsoleUpdate(bool Reset, IReadOnlyList<string> Completed, string Open)
{
    public IReadOnlyList<ConsoleFileRange>? CompletedRanges { get; init; }
    public ConsoleFileRange? OpenRange { get; init; }
}

/// <summary>
/// The live console's view of one run's log, read as it grows: each read decodes and cleans only the bytes
/// added since the last one (<see cref="RunLog.OutputCleaner"/>), so a poll's cost follows what is new, not
/// what is shown (volume-handling-conventions, Output and text length). The console shows the most recent
/// <see cref="ShownBytes"/> of output, as a terminal keeps its scrollback; the whole output stays in the log
/// and opens whole from the console. A gain larger than that is skipped to its end, since none of the
/// skipped part would stay on screen.
/// </summary>
public sealed class ConsoleFeed
{
    /// <summary>How much of a run's latest output the console shows: what is drawn, never what is kept.</summary>
    public const int ShownBytes = 256 * 1024;

    private readonly string _path;
    private readonly List<string> _completed = [];
    private readonly StringBuilder _passedOn = new();
    private Decoder _decoder = NewDecoder();
    private RunLog.OutputCleaner _cleaner;
    private long _offset = -1;
    private bool _dropFirst;
    private long _lineStart;
    private long _segmentEnd;
    private int _cancelled;
    private readonly List<ConsoleFileRange> _ranges = [];

    public void Cancel() => Interlocked.Exchange(ref _cancelled, 1);
    private void CheckCancellation()
    {
        if (Volatile.Read(ref _cancelled) != 0) throw new OperationCanceledException();
    }

    public ConsoleFeed(string path)
    {
        _path = path;
        _cleaner = NewCleaner();
    }

    private static Decoder NewDecoder() => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetDecoder();

    private RunLog.OutputCleaner NewCleaner() => new(text => _passedOn.Append(text.AsSpan(0, Math.Min(text.Length, ShownBytes - _passedOn.Length))), () =>
    {
        _completed.Add(_passedOn.ToString());
        _ranges.Add(new ConsoleFileRange(_path, _lineStart, _segmentEnd));
        _lineStart = _segmentEnd;
        _passedOn.Clear();
    });

    /// <summary>What the log gained since the last read; null when it has not changed. Throws when it cannot
    /// be read, so the caller can report it and try again on its next poll.</summary>
    public ConsoleUpdate? Read()
    {
        CheckCancellation();
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Read(stream);
    }

    internal ConsoleUpdate? Read(Stream stream)
    {
        CheckCancellation();
        var length = stream.Length;
        var reset = _offset < 0 || length < _offset || length - _offset > ShownBytes;
        if (reset)
        {
            _decoder = NewDecoder();
            _completed.Clear();
            _ranges.Clear();
            _passedOn.Clear();
            _cleaner = NewCleaner();
            _offset = Math.Max(0, length - ShownBytes);
            _lineStart = FindLineStart(stream, _offset);
            _dropFirst = _offset > 0; // the first line is cut at its start
        }
        else if (length == _offset)
            return null;

        stream.Seek(_offset, SeekOrigin.Begin);
        var bytes = new byte[64 * 1024];
        var chars = new char[bytes.Length + 1];
        var left = length - _offset;
        while (left > 0)
        {
            var read = stream.Read(bytes, 0, (int)Math.Min(bytes.Length, left));
            if (read == 0)
                break;
            CheckCancellation();
            var start = 0;
            while (start < read)
            {
                var newline = Array.IndexOf(bytes, (byte)'\n', start, read - start);
                var count = newline < 0 ? read - start : newline - start + 1;
                _segmentEnd = _offset + start + count;
                var decoded = _decoder.GetChars(bytes, start, count, chars, 0, flush: false);
                _cleaner.Write(chars.AsSpan(0, decoded));
                start += count;
            }
            left -= read;
            _offset += read;
        }

        List<string> completed = [.. _completed];
        _completed.Clear();
        List<ConsoleFileRange> ranges = [.. _ranges];
        _ranges.Clear();
        if (_dropFirst && completed.Count > 0)
        {
            completed.RemoveAt(0);
            ranges.RemoveAt(0);
            _dropFirst = false;
        }
        var open = _passedOn + _cleaner.Held;
        if (open.Length > ShownBytes) open = open[..ShownBytes];
        return new ConsoleUpdate(reset, completed, open)
        {
            CompletedRanges = ranges,
            OpenRange = new ConsoleFileRange(_path, _lineStart, _offset),
        };
    }

    private long FindLineStart(Stream stream, long offset)
    {
        var buffer = new byte[64 * 1024];
        while (offset > 0)
        {
            CheckCancellation();
            var count = (int)Math.Min(offset, buffer.Length);
            offset -= count;
            stream.Position = offset;
            stream.ReadExactly(buffer.AsSpan(0, count));
            for (var index = count - 1; index >= 0; index--)
                if (buffer[index] == (byte)'\n') return offset + index + 1;
        }
        return 0;
    }
}
