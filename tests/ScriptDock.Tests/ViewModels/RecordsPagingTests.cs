using System.Linq;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.ViewModels;
using Xunit;

namespace ScriptDock.Tests.ViewModels;

public sealed class RecordsPagingTests
{
    private static RecordSummary Log(long id, string time) =>
        new(RecordKind.Log, id, "s", time, LogLevel.Info, $"line {id}", null, null);

    private static RecordSummary Run(long id, string time) =>
        new(RecordKind.Run, id, "s", time, LogLevel.Info, "/a.command", null, null);

    [Fact]
    public void The_next_page_starts_after_the_last_record_shown()
    {
        Assert.Null(RecordsPaging.CursorAfter([]));
        Assert.Equal(new RecordCursor("t2", RecordKind.Log, 2), RecordsPaging.CursorAfter([Log(3, "t3"), Log(2, "t2")]));
    }

    [Fact]
    public void Newest_first_orders_by_time_then_kind_then_id_as_the_database_does()
    {
        var records = new[] { Log(1, "2026-10-04T08:00:00.000Z"), Run(1, "2026-10-04T08:00:00.000Z"), Log(2, "2026-10-04T08:00:00.000Z"), Log(3, "2026-10-04T07:00:00.000Z") }.ToList();

        records.Sort(RecordsPaging.NewestFirst);

        Assert.Equal(["run:1", "log:2", "log:1", "log:3"], records.Select(record => record.Key));
    }

    [Fact]
    public void A_newest_page_joins_the_rows_shown_and_keeps_the_pages_beyond_it()
    {
        var shown = new[] { Log(5, "t5"), Log(4, "t4"), Log(3, "t3") };
        var changed = Log(5, "t5") with { Title = "changed" };

        var (records, more) = RecordsPaging.MergeNewestPage(shown, shownMore: true, new RecordsPage([Log(6, "t6"), changed], More: true));

        Assert.Equal(["log:6", "log:5", "log:4", "log:3"], records.Select(record => record.Key));
        Assert.Equal("changed", records[1].Title);
        Assert.True(more);
    }

    [Fact]
    public void A_newest_page_that_reaches_past_the_rows_shown_says_whether_more_remain()
    {
        var (records, more) = RecordsPaging.MergeNewestPage([Log(5, "t5")], shownMore: true, new RecordsPage([Log(6, "t6"), Log(5, "t5")], More: false));

        Assert.Equal(2, records.Count);
        Assert.False(more);
    }
}
