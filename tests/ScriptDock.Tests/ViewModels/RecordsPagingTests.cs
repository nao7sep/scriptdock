using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.ViewModels;
using Xunit;

namespace ScriptDock.Tests.ViewModels;

public sealed class RecordsPagingTests
{
    private static RecordSummary Log(long id, string time) =>
        new(RecordKind.Log, id, "s", time, LogLevel.Info, $"line {id}", null, null);

    [Fact]
    public void The_next_page_starts_after_the_last_record_shown()
    {
        Assert.Null(RecordsPaging.CursorAfter([]));
        Assert.Equal(new RecordCursor("t2", RecordKind.Log, 2), RecordsPaging.CursorAfter([Log(3, "t3"), Log(2, "t2")]));
    }
}
