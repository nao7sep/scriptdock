using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ScriptDock.Tests.Fakes;

/// <summary>
/// A clock that moves only when a test says so. Its timers fire, on the test's own thread, as
/// <see cref="Advance"/> passes their due time; a disposed or changed timer does not.
/// </summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private readonly List<FakeTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state);
        _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>How many timers are waiting to fire.</summary>
    public int Pending => _timers.Count(timer => timer.DueAt is not null);

    public void Advance(TimeSpan by)
    {
        var until = _now + by;
        while (true)
        {
            var next = _timers
                .Where(timer => timer.DueAt is { } due && due <= until)
                .OrderBy(timer => timer.DueAt)
                .FirstOrDefault();
            if (next is null)
                break;

            _now = next.DueAt!.Value;
            next.Fire();
        }

        _now = until;
    }

    private sealed class FakeTimer(FakeTimeProvider clock, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public DateTimeOffset? DueAt { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _period = period;
            DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
            return true;
        }

        public void Fire()
        {
            DueAt = _period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero ? null : DueAt + _period;
            callback(state);
        }

        public void Dispose() => DueAt = null;

        public global::System.Threading.Tasks.ValueTask DisposeAsync()
        {
            Dispose();
            return global::System.Threading.Tasks.ValueTask.CompletedTask;
        }
    }
}
