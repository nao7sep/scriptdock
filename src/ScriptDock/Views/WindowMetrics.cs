using System;
using System.Collections.Generic;
using System.Linq;

namespace ScriptDock.Views;

/// <summary>
/// Derives the main window's minimum size from the layout itself: the minimum is the sum of the content
/// panes' real minimums plus the fixed chrome, never a hand-typed magic constant. The two body
/// columns (the Scripts/console stack and Recent) and the two left-column rows (Scripts and the
/// console) carry the real pane minimums; the header bar, status bar, splitters, and the BodyGrid
/// margin are the fixed chrome that gets reserved on top of them.
/// </summary>
/// <remarks>
/// Kept as pure functions over the live column/row minimums (read from the grids by the caller)
/// so the window minimum and the XAML track minimums can never drift apart, and so the derivation
/// can be tested without a UI thread. <see cref="MainWindow"/> assigns <see cref="MinWidthFor"/>
/// and <see cref="MinHeightFor"/> in <c>OnLoaded</c>.
/// </remarks>
public static class WindowMetrics
{
    // Native frame rounding and decoration measurements can differ from the working area slightly.
    private const double MaximizedSizeTolerance = 8;

    public static Avalonia.Controls.WindowState RestoredWindowState(bool maximized, bool isWindows) =>
        maximized && isWindows
            ? Avalonia.Controls.WindowState.Maximized
            : Avalonia.Controls.WindowState.Normal;

    public static bool CanRestoreWindowGeometry(
        int? x, int? y, double? width, double? height,
        IEnumerable<Avalonia.PixelRect> workingAreas)
    {
        if (x is not { } savedX || y is not { } savedY
            || width is not > 0 || height is not > 0
            || !double.IsFinite(width.Value) || !double.IsFinite(height.Value))
        {
            return false;
        }

        return workingAreas.Any(area =>
            area.Width > 0 && area.Height > 0
            && savedX >= area.X && savedX < (long)area.X + area.Width
            && savedY >= area.Y && savedY < (long)area.Y + area.Height);
    }

    public static bool IsMaximizedGeometry(
        Avalonia.Size frameSize, Avalonia.PixelRect workingArea, double scale)
    {
        if (scale <= 0 || !double.IsFinite(scale))
            return false;

        var workWidth = workingArea.Width / scale;
        var workHeight = workingArea.Height / scale;
        return frameSize.Width >= workWidth - MaximizedSizeTolerance
            && frameSize.Height >= workHeight - MaximizedSizeTolerance;
    }

    public static Avalonia.Size CapMinimumToWorkArea(
        Avalonia.Size contentFloor, Avalonia.PixelRect workArea, double scale, Avalonia.Size chrome) =>
        new(System.Math.Min(contentFloor.Width, System.Math.Max(1, workArea.Width / scale - chrome.Width)),
            System.Math.Min(contentFloor.Height, System.Math.Max(1, workArea.Height / scale - chrome.Height)));

    // BodyGrid has Margin="12" on all sides, so the body loses 12px on each edge horizontally
    // and vertically.
    public const double BodyMargin = 12;
    private const double BodyHorizontalMargin = BodyMargin + BodyMargin;
    private const double BodyVerticalMargin = BodyMargin + BodyMargin;

    // The GridSplitters between the two body columns and between the two left-column rows are both 6px.
    public const double ColumnSplitter = 6;
    public const double RowSplitter = 6;

    // Fixed chrome bar heights, measured from their XAML: the header Border (Padding 14,10 +
    // 1px bottom border + the ~26px wordmark/hamburger row) and the status Border (Padding 14,6 +
    // 1px top border + a single text line). These are named so the window minimum and the
    // pane-size clamps reserve the same chrome the layout actually paints.
    public const double HeaderHeight = 48;
    public const double StatusBarHeight = 31;

    /// <summary>
    /// The minimum window width: the sum of the body columns' minimum widths plus the column
    /// splitter and the BodyGrid's horizontal margin.
    /// </summary>
    public static double MinWidthFor(IEnumerable<double> columnMinWidths)
        => columnMinWidths.Sum() + ColumnSplitter + BodyHorizontalMargin;

    /// <summary>
    /// The minimum window height: the fixed chrome (header + status bar) plus the body's
    /// vertical margin, the row splitter, and the sum of the left column's row minimum heights.
    /// </summary>
    public static double MinHeightFor(
        IEnumerable<double> rowMinHeights,
        double headerHeight = HeaderHeight,
        double statusBarHeight = StatusBarHeight,
        double additionalChromeHeight = 0)
        => headerHeight + statusBarHeight + additionalChromeHeight
            + BodyVerticalMargin + RowSplitter + rowMinHeights.Sum();

    /// <summary>
    /// The widest the (fixed-size) Recent column may be at the given window width while the left
    /// column keeps its minimum — never below the Recent column's own minimum. Used both to clamp a
    /// persisted width and to re-clamp on resize, since a fixed-pixel column does not shrink itself.
    /// </summary>
    public static double MaxRecentWidth(double windowWidth, double leftColumnMin, double recentColumnMin)
        => Math.Max(recentColumnMin, windowWidth - (leftColumnMin + ColumnSplitter + BodyHorizontalMargin));

    /// <summary>
    /// The tallest the (fixed-size) console row may be at the given window height while the Scripts
    /// row keeps its minimum and the header and status bar stay reserved — never below the console
    /// row's own minimum. A fixed-pixel row does not shrink on its own, so this bounds it on resize
    /// to keep it from spilling past the window over the status bar.
    /// </summary>
    public static double MaxConsoleHeight(
        double windowHeight,
        double scriptsRowMin,
        double consoleRowMin,
        double headerHeight = HeaderHeight,
        double statusBarHeight = StatusBarHeight,
        double additionalChromeHeight = 0)
        => Math.Max(consoleRowMin, windowHeight - (headerHeight + statusBarHeight
            + additionalChromeHeight + scriptsRowMin + RowSplitter + BodyVerticalMargin));

    /// <summary>
    /// The pixel size a fixed pane should DISPLAY at: the user's stored <paramref name="intent"/>
    /// clamped to <c>[min, maxFit]</c>, where <paramref name="maxFit"/> is the room the current
    /// window leaves (from <see cref="MaxRecentWidth"/> / <see cref="MaxConsoleHeight"/>). Window-shrink
    /// narrows the display toward <paramref name="min"/>; window-grow returns it toward intent. The
    /// intent itself is never altered here — only a real splitter drag updates it — so a temporary
    /// shrink can never be persisted as the new intent.
    /// </summary>
    /// <remarks>
    /// <paramref name="maxFit"/> already floors at <paramref name="min"/>, so when the window is so
    /// small that there is no room beyond the minimum, the display lands exactly on the minimum.
    /// </remarks>
    public static double DisplayFromIntent(double intent, double min, double maxFit)
        => Math.Clamp(intent, min, Math.Max(min, maxFit));
}
