using System;
using ScriptDock.I18n;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.ViewModels;
using Xunit;

namespace ScriptDock.Tests.ViewModels;

/// <summary>A Recent row's state pill: the live process when there is one, otherwise the recorded end.</summary>
public sealed class RecentEntryTests
{
    private static RecentEntry Recorded(RecordedEnd? end) =>
        new("/x/a.command", "a.command", DateTimeOffset.UnixEpoch, process: null, lastEnd: end);

    [Theory]
    [InlineData("exited", 0, "recent.stateExited", false)]
    [InlineData("exited", null, "recent.stateExited", false)]
    [InlineData("terminated", null, "recent.stateStopped", false)]
    [InlineData("failed", null, "recent.stateFailed", true)]
    public void With_no_live_process_the_pill_reads_the_recorded_end(string state, int? code, string key, bool failed)
    {
        var entry = Recorded(new RecordedEnd(state, code));

        Assert.Equal(Localizer.T(key), entry.StatePillText);
        Assert.Equal(failed, entry.IsPillFailed);
        Assert.False(entry.IsRunning);
    }

    [Fact]
    public void A_recorded_non_zero_exit_reads_its_code_and_tints_red()
    {
        var entry = Recorded(new RecordedEnd("exited", 3));

        Assert.Equal(Localizer.T("recent.stateExitedWithCode", ("code", 3)), entry.StatePillText);
        Assert.True(entry.IsPillFailed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("gone")]
    public void No_recorded_end_or_an_old_gone_reads_unknown(string? state)
    {
        var entry = Recorded(state is null ? null : new RecordedEnd(state, null));

        Assert.Equal(Localizer.T("recent.stateUnknown"), entry.StatePillText);
        Assert.False(entry.IsPillFailed);
        Assert.False(entry.IsRunning);
    }

    [Fact]
    public void A_live_process_wins_over_the_recorded_end()
    {
        var running = new ScriptProcess(1, "/x/a.command", DateTimeOffset.UnixEpoch);
        var entry = new RecentEntry("/x/a.command", "a.command", DateTimeOffset.UnixEpoch, running, new RecordedEnd("failed", null));

        Assert.True(entry.IsRunning);
        Assert.Equal(Localizer.T("recent.stateRunning"), entry.StatePillText);
    }
}
