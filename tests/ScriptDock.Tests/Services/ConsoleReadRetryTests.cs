using System;
using ScriptDock.Services;
using Xunit;

namespace ScriptDock.Tests.Services;

public sealed class ConsoleReadRetryTests
{
    private sealed class Clock : TimeProvider
    {
        public TimeSpan Elapsed;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Elapsed.Ticks;
    }

    [Fact]
    public void Persistent_failure_backs_off_and_reports_once_until_recovery()
    {
        var retry = new ConsoleReadRetry();
        var now = new Clock();
        Assert.True(retry.CanRead(now));
        Assert.True(retry.Failed(now));
        now.Elapsed += TimeSpan.FromMilliseconds(300);
        Assert.False(retry.CanRead(now));
        now.Elapsed += TimeSpan.FromSeconds(1);
        Assert.True(retry.CanRead(now));
        Assert.False(retry.Failed(now));
        now.Elapsed += TimeSpan.FromSeconds(1);
        Assert.False(retry.CanRead(now));
        for (var i = 0; i < 20; i++)
        {
            now.Elapsed += TimeSpan.FromSeconds(30);
            Assert.True(retry.CanRead(now));
            Assert.False(retry.Failed(now));
        }
        retry.Reset();
        Assert.True(retry.CanRead(now));
        Assert.True(retry.Failed(now));
    }
}
