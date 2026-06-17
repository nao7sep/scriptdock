using System.Collections.Generic;
using System.Linq;

namespace ScriptDock.Services;

/// <summary>
/// Bounded, thread-safe store of a process's recent output lines. ANSI escapes are
/// stripped on the way in; once the line count exceeds the cap the oldest lines are
/// dropped (counted in <see cref="DroppedCount"/>) so a chatty dev server cannot grow
/// memory without bound. Output is retained after the process exits so a finished run can
/// be read in-app and then dismissed.
/// </summary>
public sealed class OutputBuffer
{
    public const int DefaultMaxLines = 5000;

    private readonly int _maxLines;
    private readonly Queue<string> _lines = new();
    private readonly object _gate = new();

    public OutputBuffer(int maxLines = DefaultMaxLines) => _maxLines = maxLines;

    public int DroppedCount { get; private set; }

    public void AppendLine(string rawLine)
    {
        var clean = AnsiStripper.Strip(rawLine);
        lock (_gate)
        {
            _lines.Enqueue(clean);
            while (_lines.Count > _maxLines)
            {
                _lines.Dequeue();
                DroppedCount++;
            }
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
            return _lines.ToList();
    }
}
