using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ScriptDock.Views;
using Xunit;

namespace ScriptDock.Tests.Views;

/// <summary>
/// The window's minimum size is derived, not guessed (per the window-chrome conventions):
/// <see cref="WindowMetrics"/> sums the live grid column/row minimums plus the fixed chrome
/// (header bar, status bar, splitters, body margin) so the window can never shrink small enough
/// to hide a pane or overlap the status bar, and the restore clamp in <c>MainWindow.OnLoaded</c>
/// reuses the same constants for its bounds. These tests pin the derivation math directly (no
/// Avalonia headless harness, matching the suite's pure-helper style), guard that every body
/// column and left-column row declares a non-zero minimum, and assert the live XAML track minimums
/// match the values the helper and clamp are written against — so a future change to a minimum, a
/// splitter, or the margin fails here rather than silently letting the window under-size or the
/// clamp drift away from the layout.
/// </summary>
public sealed class WindowMetricsTests
{
    // Mirrors the live layout in Views/MainWindow.axaml. Kept here so the derivation assertions
    // read against a concrete, known set; the XAML guards below are what catch drift between this
    // list and the actual XAML.
    // These are the XAML floor values; OnLoaded raises the column minimums further to fit each
    // list header's measured width. The console row min is 160 (header + stdin input + a few lines).
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
    public void RestoreClampBounds_EqualTheLayoutTrackMinimums()
    {
        // The restore clamp in MainWindow.OnLoaded uses each pane's own track minimum as the lower
        // bound (Recent column min for the right pane, console row min for the console). Those lower
        // bounds must equal what the XAML actually declares, so the clamp can never let a restored
        // size fall below the minimum the layout enforces.
        var axaml = ReadMainWindowAxaml();
        var columnMins = BodyColumnMinWidths(axaml);
        var rowMins = LeftColumnRowMinHeights(axaml);

        Assert.Equal(260d, columnMins[1]); // Recent column min == clamp lower bound for SavedRecentWidth
        Assert.Equal(160d, rowMins[1]);    // console row min == clamp lower bound for SavedConsoleHeight
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

    [Fact]
    public void EveryBodyColumnAndLeftColumnRow_DeclaresANonZeroMinimum()
    {
        // Guard against a column or row being added/changed without a minimum: such a track would
        // contribute 0 to the derived window minimum and could be squeezed to invisibility.
        var axaml = ReadMainWindowAxaml();
        var columnMins = BodyColumnMinWidths(axaml);
        var rowMins = LeftColumnRowMinHeights(axaml);

        Assert.NotEmpty(columnMins);
        Assert.NotEmpty(rowMins);
        Assert.All(columnMins, m => Assert.True(m > 0, "A BodyGrid column is missing a non-zero MinWidth."));
        Assert.All(rowMins, m => Assert.True(m > 0, "A LeftPanesGrid row is missing a non-zero MinHeight."));
    }

    [Fact]
    public void DerivedMinimums_MatchTheLiveLayoutMinimums()
    {
        // The mirrored lists used above must stay equal to what the XAML actually declares, so the
        // derivation tests cannot pass against a stale list.
        var axaml = ReadMainWindowAxaml();
        Assert.Equal(ColumnMinWidths, BodyColumnMinWidths(axaml));
        Assert.Equal(RowMinHeights, LeftColumnRowMinHeights(axaml));
    }

    [Fact]
    public void Body_IsColumnMajor_WithRecentBesideTheLeftScriptsOverConsoleStack()
    {
        // The layout's defining shape: the body splits into COLUMNS (left stack | splitter | Recent),
        // and the left column splits into ROWS (Scripts | splitter | console). This is what makes
        // Recent a full-body-height column rather than a top-right pane. Pin it so a regression to the
        // old row-major body (Recent nested beside Scripts in a top row) fails here. The same shape is
        // what lets WindowMetrics read width from BodyGrid's columns and height from LeftPanesGrid's rows.
        var axaml = ReadMainWindowAxaml();

        // BodyGrid's own track definitions are columns, not rows.
        var bodyGridHead = Section(axaml, "<Grid x:Name=\"BodyGrid\"", "</Grid.ColumnDefinitions>");
        Assert.DoesNotContain("<Grid.RowDefinitions>", bodyGridHead);

        // The left column is an inner grid that splits into rows.
        Assert.Contains("<Grid x:Name=\"LeftPanesGrid\"", axaml);
        var leftGridHead = Section(axaml, "<Grid x:Name=\"LeftPanesGrid\"", "</Grid.RowDefinitions>");
        Assert.DoesNotContain("<Grid.ColumnDefinitions>", leftGridHead);

        // Recent sits in the third body column (it is the full-height right pane), and the column
        // splitter resizes columns while the console splitter resizes rows.
        Assert.Contains("<Border Grid.Column=\"2\" Classes=\"card\"", axaml);
        Assert.Contains("x:Name=\"RecentSplitter\"", axaml);
        Assert.Contains("x:Name=\"ConsoleSplitter\"", axaml);
        Assert.Matches("RecentSplitter[^>]*ResizeDirection=\"Columns\"", axaml);
        Assert.Matches("ConsoleSplitter[^>]*ResizeDirection=\"Rows\"", axaml);
    }

    // The two BodyGrid columns that carry a MinWidth (the 6px splitter column has none), in order:
    // the left Scripts/console stack and the Recent column.
    private static IReadOnlyList<double> BodyColumnMinWidths(string axaml)
    {
        var bodyGrid = Section(axaml, "<Grid x:Name=\"BodyGrid\"", "</Grid.ColumnDefinitions>");
        return Regex.Matches(bodyGrid, "<ColumnDefinition\\b[^>]*?MinWidth=\"(?<min>\\d+(?:\\.\\d+)?)\"")
            .Select(m => double.Parse(m.Groups["min"].Value, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
    }

    // The two LeftPanesGrid rows that carry a MinHeight (the 6px splitter row has none), in order:
    // the Scripts row and the console row.
    private static IReadOnlyList<double> LeftColumnRowMinHeights(string axaml)
    {
        var leftGrid = Section(axaml, "<Grid x:Name=\"LeftPanesGrid\"", "</Grid.RowDefinitions>");
        return Regex.Matches(leftGrid, "<RowDefinition\\b[^>]*?MinHeight=\"(?<min>\\d+(?:\\.\\d+)?)\"")
            .Select(m => double.Parse(m.Groups["min"].Value, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
    }

    private static string Section(string text, string startMarker, string endMarker)
    {
        var start = text.IndexOf(startMarker, System.StringComparison.Ordinal);
        Assert.True(start >= 0, $"Marker not found in MainWindow.axaml: {startMarker}");
        var end = text.IndexOf(endMarker, start, System.StringComparison.Ordinal);
        Assert.True(end >= 0, $"Marker not found in MainWindow.axaml: {endMarker}");
        return text[start..end];
    }

    private static string ReadMainWindowAxaml([CallerFilePath] string callerPath = "")
    {
        // This file: <repo>/tests/ScriptDock.Tests/Views/WindowMetricsTests.cs
        // Target:    <repo>/Views/MainWindow.axaml
        var testsViewsDir = Path.GetDirectoryName(callerPath)!;
        var repoRoot = Path.GetFullPath(Path.Combine(testsViewsDir, "..", "..", ".."));
        return File.ReadAllText(Path.Combine(repoRoot, "Views", "MainWindow.axaml"));
    }
}
