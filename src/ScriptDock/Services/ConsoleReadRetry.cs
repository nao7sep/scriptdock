using System;

namespace ScriptDock.Services;

/// <summary>One console feed's failed-read episode. Normal polling resumes after recovery; repeated
/// failure backs off without repeatedly recording the same unavailable output.</summary>
internal sealed class ConsoleReadRetry
{
    private int _failures;
    private long _failedAt;
    private TimeSpan _delay;
    internal bool CanRead(TimeProvider time) => _failures == 0 || time.GetElapsedTime(_failedAt) >= _delay;
    internal bool Failed(TimeProvider time)
    {
        var first = _failures == 0;
        _failures = Math.Min(_failures + 1, 8);
        _failedAt = time.GetTimestamp();
        _delay = TimeSpan.FromSeconds(Math.Min(30, 0.3 * Math.Pow(2, _failures)));
        return first;
    }
    internal void Reset() { _failures = 0; _delay = default; }
}
