using System;

namespace ScriptDock.ViewModels;

/// <summary>
/// The Records window's layout (window-conventions, Content-based minimum size): a list pane the user sizes
/// with its splitter beside the detail pane, which takes the rest. The window's minimum is the panes'
/// minimums plus the margin and splitter between them, and the list's display width is its saved intent
/// clamped to what the window leaves it.
/// </summary>
public static class RecordsLayout
{
    public const double ListMinWidth = 320;
    public const double ListDefaultWidth = 380;
    public const double ListMaxWidth = 640;
    public const double DetailMinWidth = 420;

    /// <summary>The window's margin around both panes, and the splitter column between them.</summary>
    public const double Margin = 12;
    public const double Splitter = 6;

    /// <summary>Below the filters, the list keeps room for a few rows; the detail pane for its header and a
    /// few fields. Both panes' 1px card edges count toward the window's height.</summary>
    public const double ListMinHeight = 180;
    public const double DetailMinHeight = 220;
    private const double CardEdges = 2;

    /// <summary>A saved list width, healed into its bounds; none, or one that is not a number, is the default.</summary>
    public static double ClampListWidth(double? saved) =>
        saved is { } width && double.IsFinite(width)
            ? Math.Round(Math.Clamp(width, ListMinWidth, ListMaxWidth))
            : ListDefaultWidth;

    /// <summary>The widest the list may be at <paramref name="windowWidth"/> while the detail pane keeps its minimum.</summary>
    public static double MaxListWidth(double windowWidth) =>
        Math.Clamp(windowWidth - 2 * Margin - Splitter - DetailMinWidth, ListMinWidth, ListMaxWidth);

    /// <summary>The width the list shows at: the user's intent, narrowed while the window cannot fit it.</summary>
    public static double DisplayListWidth(double intent, double windowWidth) =>
        Math.Clamp(intent, ListMinWidth, MaxListWidth(windowWidth));

    public const double MinWindowWidth = 2 * Margin + ListMinWidth + Splitter + DetailMinWidth;

    /// <summary>The window's minimum height, with the filters above the list measured in the current font.</summary>
    public static double MinWindowHeight(double filtersHeight) =>
        2 * Margin + CardEdges + Math.Max(filtersHeight + ListMinHeight, DetailMinHeight);

    /// <summary>Whether the list is scrolled to its top, where new records may appear without moving it.</summary>
    public static bool AtTop(double offset) => offset < 1;

    /// <summary>Whether the list is within about one screen of the end of what is loaded.</summary>
    public static bool NearEnd(double extent, double offset, double viewport) => extent - offset - viewport <= viewport;
}
