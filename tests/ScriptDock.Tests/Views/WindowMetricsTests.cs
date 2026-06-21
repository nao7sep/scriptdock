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
/// Avalonia headless harness, matching the suite's pure-helper style), guard that every list
/// column and body row declares a non-zero minimum, and assert the live XAML track minimums match
/// the values the helper and clamp are written against — so a future change to a minimum, a
/// splitter, or the margin fails here rather than silently letting the window under-size or the
/// clamp drift away from the layout.
/// </summary>
public sealed class WindowMetricsTests
{
    // Mirrors the live layout in Views/MainWindow.axaml. Kept here so the derivation assertions
    // read against a concrete, known set; the XAML guards below are what catch drift between this
    // list and the actual XAML.
    // These are the XAML floor values; OnLoaded raises the column minimums further to fit each
    // pane header's measured width. The console row min is 160 (header + stdin input + a few lines).
    private static readonly double[] ColumnMinWidths = [360, 260]; // Scripts, Recent
    private static readonly double[] RowMinHeights = [200, 160];   // lists row, console row

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
        var columnMins = ListsColumnMinWidths(axaml);
        var rowMins = BodyRowMinHeights(axaml);

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
    public void MaxConsoleHeight_GivesBackTheRoomBelowTheLists_ButNeverBelowItsMinimum()
    {
        const double listsMin = 200, consoleMin = 160;
        // A tall window: the console may grow into the room left after chrome + the lists' min.
        var tall = WindowMetrics.MaxConsoleHeight(900, listsMin, consoleMin);
        var chrome = WindowMetrics.HeaderHeight + WindowMetrics.StatusBarHeight
            + WindowMetrics.RowSplitter + 2 * WindowMetrics.BodyMargin;
        Assert.Equal(900 - (chrome + listsMin), tall);
        // A short window: the cap never drops below the console's own minimum (so it can't be hidden,
        // and the window minimum keeps the whole body — including the status bar — visible).
        Assert.Equal(consoleMin, WindowMetrics.MaxConsoleHeight(400, listsMin, consoleMin));
    }

    [Fact]
    public void EveryListColumnAndBodyRow_DeclaresANonZeroMinimum()
    {
        // Guard against a column or row being added/changed without a minimum: such a track would
        // contribute 0 to the derived window minimum and could be squeezed to invisibility.
        var axaml = ReadMainWindowAxaml();
        var columnMins = ListsColumnMinWidths(axaml);
        var rowMins = BodyRowMinHeights(axaml);

        Assert.NotEmpty(columnMins);
        Assert.NotEmpty(rowMins);
        Assert.All(columnMins, m => Assert.True(m > 0, "A ListsGrid column is missing a non-zero MinWidth."));
        Assert.All(rowMins, m => Assert.True(m > 0, "A BodyGrid content row is missing a non-zero MinHeight."));
    }

    [Fact]
    public void DerivedMinimums_MatchTheLiveLayoutMinimums()
    {
        // The mirrored lists used above must stay equal to what the XAML actually declares, so the
        // derivation tests cannot pass against a stale list.
        var axaml = ReadMainWindowAxaml();
        Assert.Equal(ColumnMinWidths, ListsColumnMinWidths(axaml));
        Assert.Equal(RowMinHeights, BodyRowMinHeights(axaml));
    }

    // The two ListsGrid columns that carry a MinWidth (the 6px splitter column has none), in order.
    private static IReadOnlyList<double> ListsColumnMinWidths(string axaml)
    {
        var listsGrid = Section(axaml, "<Grid x:Name=\"ListsGrid\"", "</Grid.ColumnDefinitions>");
        return Regex.Matches(listsGrid, "<ColumnDefinition\\b[^>]*?MinWidth=\"(?<min>\\d+(?:\\.\\d+)?)\"")
            .Select(m => double.Parse(m.Groups["min"].Value, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
    }

    // The two BodyGrid content rows that carry a MinHeight (the 6px splitter row has none), in order.
    private static IReadOnlyList<double> BodyRowMinHeights(string axaml)
    {
        var bodyGrid = Section(axaml, "<Grid.RowDefinitions>", "</Grid.RowDefinitions>");
        return Regex.Matches(bodyGrid, "<RowDefinition\\b[^>]*?MinHeight=\"(?<min>\\d+(?:\\.\\d+)?)\"")
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
