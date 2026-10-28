using System;

namespace ScriptDock.Services;

/// <summary>
/// The exit sequence's one deadline, fixed when a quit begins: every step of the quit, and the owners'
/// shutdown after the window has gone, draw on what remains of it (architecture-conventions, Deadlines at the
/// owner).
/// </summary>
public sealed class ExitDeadline
{
    /// <summary>How long a quit may take in all, from the moment it begins.</summary>
    public static readonly TimeSpan Length = TimeSpan.FromSeconds(3);

    private readonly TimeProvider _time;
    private readonly long _started;

    public ExitDeadline(TimeProvider time)
    {
        _time = time;
        _started = time.GetTimestamp();
    }

    /// <summary>What remains of the deadline, never less than zero.</summary>
    public TimeSpan Remaining
    {
        get
        {
            var left = Length - _time.GetElapsedTime(_started);
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    /// <summary>The operating system is ending the session, so the exit does not wait for optional history.</summary>
    public bool SessionEnding { get; set; }
}
