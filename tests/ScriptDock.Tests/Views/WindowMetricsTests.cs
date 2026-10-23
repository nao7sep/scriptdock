using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using ScriptDock.Views;
using Xunit;

namespace ScriptDock.Tests.Views;

/// <summary>
/// The window's minimum size is derived, not guessed: <see cref="WindowMetrics"/> sums the live grid
/// column and row minimums plus the fixed chrome (header bar, status bar, splitters, body margin), so the
/// window can never shrink small enough to hide a pane or overlap the status bar. These pin the
/// derivation arithmetic and check, on the real window, that every pane track has a minimum.
/// </summary>
public sealed class WindowMetricsTests
{
    [Theory]
    [InlineData(false, false, Avalonia.Controls.WindowState.Normal)]
    [InlineData(true, false, Avalonia.Controls.WindowState.Normal)]
    [InlineData(false, true, Avalonia.Controls.WindowState.Normal)]
    [InlineData(true, true, Avalonia.Controls.WindowState.Maximized)]
    public void Restored_window_state_is_maximized_only_on_Windows(
        bool maximized, bool isWindows, Avalonia.Controls.WindowState expected)
    {
        Assert.Equal(expected, WindowMetrics.RestoredWindowState(maximized, isWindows));
    }

    [Fact]
    public void WindowGeometry_AllowsNegativeCoordinatesOnAConnectedDisplay()
    {
        (Avalonia.PixelRect, double)[] screens =
        [
            (new(0, 0, 1920, 1080), 1),
            (new(-1600, 40, 1600, 1000), 1),
        ];

        Assert.True(WindowMetrics.CanRestoreWindowGeometry(
            -1400, 120, 1100, 720, screens));
    }

    [Theory]
    [InlineData(1919, 1079, 1.0, false)] // only a corner left on screen: the reported bug
    [InlineData(1801, 100, 1.0, false)]  // 119 DIP of title bar showing
    [InlineData(1800, 100, 1.0, true)]   // 120 DIP showing
    [InlineData(100, 1033, 1.0, false)]  // title band 47 DIP above the bottom
    [InlineData(100, 1032, 1.0, true)]   // 48 DIP
    [InlineData(100, -1, 1.0, false)]    // title bar above the working area
    [InlineData(-7, 100, 1.0, true)]     // flush left on Windows, past the invisible resize border
    [InlineData(1681, 100, 2.0, false)]  // at scale 2 the grab is 240 px
    [InlineData(1680, 100, 2.0, true)]
    public void WindowGeometry_NeedsAGrabbableTitleBar(int x, int y, double scale, bool expected)
    {
        Assert.Equal(expected, WindowMetrics.CanRestoreWindowGeometry(
            x, y, 1100, 720, [(new Avalonia.PixelRect(0, 0, 1920, 1080), scale)]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(double.NaN)]
    public void WindowGeometry_RejectsAnUnusableScale(double scale)
    {
        Assert.False(WindowMetrics.CanRestoreWindowGeometry(
            100, 100, 1100, 720, [(new Avalonia.PixelRect(0, 0, 1920, 1080), scale)]));
    }

    [Theory]
    [InlineData(null, 20, 1100.0, 720.0)]
    [InlineData(20, null, 1100.0, 720.0)]
    [InlineData(20, 20, null, 720.0)]
    [InlineData(20, 20, 1100.0, null)]
    [InlineData(20, 20, 0.0, 720.0)]
    [InlineData(20, 20, 1100.0, -1.0)]
    [InlineData(3000, 20, 1100.0, 720.0)]
    public void WindowGeometry_RejectsIncompleteInvalidOrDisconnectedValues(
        int? x, int? y, double? width, double? height)
    {
        Assert.False(WindowMetrics.CanRestoreWindowGeometry(
            x, y, width, height, [(new Avalonia.PixelRect(0, 0, 1920, 1080), 1.0)]));
    }

    [Theory]
    [InlineData(1280, 720, 2560, 1280, 1, false)]
    [InlineData(2557, 1276, 2560, 1280, 1, true)]
    [InlineData(1278, 636, 2560, 1280, 2, true)]
    [InlineData(2560, 1280, 2560, 1280, 0, false)]
    public void Maximized_geometry_matches_the_scaled_working_area(
        double width, double height, int workWidth, int workHeight, double scale, bool expected)
    {
        Assert.Equal(expected, WindowMetrics.IsMaximizedGeometry(
            new Avalonia.Size(width, height),
            new Avalonia.PixelRect(0, 30, workWidth, workHeight),
            scale));
    }

    // Sample pane minimums for the derivation arithmetic, which takes whatever the live tracks give it.
    private static readonly double[] ColumnMinWidths = [360, 260]; // left column (Scripts/Output), Recent
    private static readonly double[] RowMinHeights = [200, 160];   // Scripts row, console row

    [Fact]
    public void MinWidth_EqualsColumnMinimumsPlusSplitterAndMargin()
    {
        // column mins + the single 6px column splitter + the BodyGrid's 12px left+right margin.
        var expected = ColumnMinWidths.Sum() + WindowMetrics.ColumnSplitter + 2 * WindowMetrics.BodyMargin;
        Assert.Equal(expected, WindowMetrics.MinWidthFor(ColumnMinWidths));
    }

    [Fact]
    public void MinWidth_TracksTheColumnsItIsGiven()
    {
        // Adding a column to the input must move the derived minimum by exactly that column's
        // minimum width — the property that keeps the window and its columns from drifting apart.
        var baseWidth = WindowMetrics.MinWidthFor(ColumnMinWidths);
        var widened = WindowMetrics.MinWidthFor([.. ColumnMinWidths, 75]);
        Assert.Equal(baseWidth + 75, widened);
    }

    [Fact]
    public void MinHeight_EqualsChromePlusRowMinimumsSplitterAndMargin()
    {
        // header + status bar + 12px top+bottom body margin + the single 6px row splitter + row mins.
        var expected = WindowMetrics.HeaderHeight + WindowMetrics.StatusBarHeight
            + 2 * WindowMetrics.BodyMargin + WindowMetrics.RowSplitter + RowMinHeights.Sum();
        Assert.Equal(expected, WindowMetrics.MinHeightFor(RowMinHeights));
    }

    [Fact]
    public void MinHeight_TracksTheRowsItIsGiven()
    {
        var baseHeight = WindowMetrics.MinHeightFor(RowMinHeights);
        var taller = WindowMetrics.MinHeightFor([.. RowMinHeights, 90]);
        Assert.Equal(baseHeight + 90, taller);
    }

    [Fact]
    public void MinHeight_UsesLiveChromeAndReservesPersistentErrorRow()
    {
        const double measuredHeader = 56;
        const double measuredStatus = 35;
        const double errorRow = 38;
        var expected = measuredHeader + measuredStatus + errorRow
            + 2 * WindowMetrics.BodyMargin + WindowMetrics.RowSplitter + RowMinHeights.Sum();

        Assert.Equal(
            expected,
            WindowMetrics.MinHeightFor(RowMinHeights, measuredHeader, measuredStatus, errorRow));
    }

    [Fact]
    public void MinHeight_ReservesEveryPanePlusChrome_SoNoPaneCanBeHidden()
    {
        // Beyond the exact-sum identity, the invariant the conventions care about: the minimum is
        // at least the tallest single pane plus all the fixed chrome, so even the largest pane is
        // always fully visible above the reserved header and status bar.
        var chrome = WindowMetrics.HeaderHeight + WindowMetrics.StatusBarHeight
            + 2 * WindowMetrics.BodyMargin + WindowMetrics.RowSplitter;
        var minHeight = WindowMetrics.MinHeightFor(RowMinHeights);
        Assert.True(minHeight >= RowMinHeights.Max() + chrome);
    }

    [Fact]
    public void MaxRecentWidth_GivesBackTheRoomBesideScripts_ButNeverBelowItsMinimum()
    {
        const double scriptsMin = 360, recentMin = 260;
        // A wide window: Recent may grow into the room left after Scripts' min, the splitter, and the margin.
        var wide = WindowMetrics.MaxRecentWidth(1200, scriptsMin, recentMin);
        Assert.Equal(1200 - (scriptsMin + WindowMetrics.ColumnSplitter + 2 * WindowMetrics.BodyMargin), wide);
        // A narrow window: the cap never drops below Recent's own minimum (Scripts gives way instead).
        Assert.Equal(recentMin, WindowMetrics.MaxRecentWidth(500, scriptsMin, recentMin));
    }

    [Fact]
    public void MaxConsoleHeight_GivesBackTheRoomBelowTheScripts_ButNeverBelowItsMinimum()
    {
        const double scriptsMin = 200, consoleMin = 160;
        // A tall window: the console may grow into the room left after chrome + the Scripts row's min.
        var tall = WindowMetrics.MaxConsoleHeight(900, scriptsMin, consoleMin);
        var chrome = WindowMetrics.HeaderHeight + WindowMetrics.StatusBarHeight
            + WindowMetrics.RowSplitter + 2 * WindowMetrics.BodyMargin;
        Assert.Equal(900 - (chrome + scriptsMin), tall);
        // A short window: the cap never drops below the console's own minimum (so it can't be hidden,
        // and the window minimum keeps the whole body — including the status bar — visible).
        Assert.Equal(consoleMin, WindowMetrics.MaxConsoleHeight(400, scriptsMin, consoleMin));
    }

    [Fact]
    public void MaxConsoleHeight_UsesTheSameLiveChromeAsTheNativeMinimum()
    {
        const double windowHeight = 900;
        const double scriptsMin = 200;
        const double consoleMin = 160;
        const double header = 56;
        const double status = 35;
        const double errorRow = 38;

        Assert.Equal(
            windowHeight - (header + status + errorRow + scriptsMin
                + WindowMetrics.RowSplitter + 2 * WindowMetrics.BodyMargin),
            WindowMetrics.MaxConsoleHeight(
                windowHeight, scriptsMin, consoleMin, header, status, errorRow));
    }

    [Fact]
    public void DisplayFromIntent_ShowsTheIntentWhenItFits()
    {
        // Window big enough to honour the user's intent: the display equals the intent exactly,
        // neither clamped up to the min nor down to the fit.
        const double intent = 460, min = 260;
        var maxFit = WindowMetrics.MaxRecentWidth(1400, 360, min); // ample room
        Assert.Equal(intent, WindowMetrics.DisplayFromIntent(intent, min, maxFit));
    }

    [Fact]
    public void DisplayFromIntent_NarrowsTowardFit_WhenTheWindowIsTooSmall_ButLeavesIntentUntouched()
    {
        // The user wants 460px, but a small window only fits ~maxFit. The DISPLAY drops to maxFit
        // while the stored intent (the argument) is, by construction, never mutated — the helper is
        // pure, so the resize path that calls it cannot change what the user intended.
        const double intent = 460, min = 260;
        var maxFit = WindowMetrics.MaxRecentWidth(620, 360, min); // tight: less room than the intent
        var display = WindowMetrics.DisplayFromIntent(intent, min, maxFit);
        Assert.True(display < intent);   // narrowed for the small window
        Assert.Equal(maxFit, display);   // exactly the room the window leaves
        Assert.Equal(460, intent);       // the intent value is unchanged by the derivation
    }

    [Fact]
    public void DisplayFromIntent_ReturnsToIntent_WhenTheWindowGrowsBack()
    {
        // The core regression guard: deriving the display from the SAME stored intent must give the
        // narrowed size on a small window and the full intent again once the window grows — so a
        // shrink-then-grow returns the pane to where the user left it (the old code clamped down on
        // shrink and never came back, and persisted that clamped value).
        const double intent = 460, scriptsMin = 360, recentMin = 260;
        var small = WindowMetrics.DisplayFromIntent(
            intent, recentMin, WindowMetrics.MaxRecentWidth(620, scriptsMin, recentMin));
        var large = WindowMetrics.DisplayFromIntent(
            intent, recentMin, WindowMetrics.MaxRecentWidth(1400, scriptsMin, recentMin));
        Assert.True(small < intent);
        Assert.Equal(intent, large);
    }

    [Fact]
    public void DisplayFromIntent_NeverDropsBelowTheMinimum_EvenWhenFitWouldRoundUpToIt()
    {
        // maxFit already floors at min; the helper must still never return below min for any intent.
        const double min = 260;
        Assert.Equal(min, WindowMetrics.DisplayFromIntent(100, min, min)); // tiny intent, no room
        Assert.Equal(min, WindowMetrics.DisplayFromIntent(500, min, min)); // big intent, no room
    }

    // A column or row without a minimum would add nothing to the derived window minimum and could be
    // squeezed to nothing; the splitter tracks are the only ones without.
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void Every_pane_track_of_the_main_window_has_a_minimum()
    {
        var window = new MainWindow();
        var columns = window.FindControl<Avalonia.Controls.Grid>("BodyGrid")!.ColumnDefinitions;
        var rows = window.FindControl<Avalonia.Controls.Grid>("LeftPanesGrid")!.RowDefinitions;

        Assert.All(columns.Where(column => column.Width.Value != WindowMetrics.ColumnSplitter), column => Assert.True(column.MinWidth > 0));
        Assert.All(rows.Where(row => row.Height.Value != WindowMetrics.RowSplitter), row => Assert.True(row.MinHeight > 0));
        Assert.Equal(Avalonia.Controls.WindowStartupLocation.Manual, window.WindowStartupLocation); // the system places it
    }

    // A dialog is its own OS window, so nothing but this keeps it inside the screen it opens on.
    [Theory]
    [InlineData(1200d, 1d, 1020d)]
    // A high-DPI screen reports physical pixels; the bound is in the logical units MaxHeight takes.
    [InlineData(2400d, 2d, 1020d)]
    public void A_dialog_is_bounded_by_the_screen_it_opens_on(
        double workingAreaHeight, double scale, double expected)
    {
        Assert.Equal(expected, WindowMetrics.DialogMaxHeight(workingAreaHeight, scale));
    }

    // A screen the toolkit cannot report leaves the dialog its natural height rather than a guess.
    [Theory]
    [InlineData(0d, 0d)]
    [InlineData(1200d, 0d)]
    [InlineData(0d, 1d)]
    [InlineData(1200d, double.NaN)]
    public void An_unreadable_screen_leaves_a_dialog_unbounded(double workingAreaHeight, double scale)
    {
        Assert.Equal(double.PositiveInfinity, WindowMetrics.DialogMaxHeight(workingAreaHeight, scale));
    }
}
