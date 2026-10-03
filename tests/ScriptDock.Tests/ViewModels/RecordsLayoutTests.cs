using ScriptDock.ViewModels;
using Xunit;

namespace ScriptDock.Tests.ViewModels;

public sealed class RecordsLayoutTests
{
    [Fact]
    public void A_saved_list_width_is_healed_into_its_bounds()
    {
        Assert.Equal(451, RecordsLayout.ClampListWidth(450.6));
        Assert.Equal(RecordsLayout.ListMinWidth, RecordsLayout.ClampListWidth(10));
        Assert.Equal(RecordsLayout.ListMaxWidth, RecordsLayout.ClampListWidth(5000));
        Assert.Equal(RecordsLayout.ListDefaultWidth, RecordsLayout.ClampListWidth(null));
        Assert.Equal(RecordsLayout.ListDefaultWidth, RecordsLayout.ClampListWidth(double.NaN));
    }

    [Fact]
    public void The_window_minimum_is_the_panes_minimums_plus_the_margin_and_splitter()
    {
        Assert.Equal(12 + 320 + 6 + 420 + 12, RecordsLayout.MinWindowWidth);
        Assert.Equal(12 + 12 + 2 + 120 + 180, RecordsLayout.MinWindowHeight(120));
    }

    [Fact]
    public void A_narrow_window_narrows_the_list_and_a_wide_one_returns_it_to_the_intent()
    {
        Assert.Equal(RecordsLayout.ListMinWidth, RecordsLayout.DisplayListWidth(600, RecordsLayout.MinWindowWidth));
        Assert.Equal(400, RecordsLayout.DisplayListWidth(600, RecordsLayout.MinWindowWidth + 80));
        Assert.Equal(600, RecordsLayout.DisplayListWidth(600, 2000));
    }

    [Fact]
    public void The_list_is_at_its_top_and_near_its_end_by_its_scroll_offset()
    {
        Assert.True(RecordsLayout.AtTop(0.4));
        Assert.False(RecordsLayout.AtTop(1));
        Assert.True(RecordsLayout.NearEnd(extent: 1000, offset: 600, viewport: 200));
        Assert.False(RecordsLayout.NearEnd(extent: 1000, offset: 500, viewport: 200));
        Assert.True(RecordsLayout.NearEnd(extent: 150, offset: 0, viewport: 200));
    }
}
