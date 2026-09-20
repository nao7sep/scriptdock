using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace ScriptDock.Tests;

public sealed class AppStylesTests : WindowTest
{
    // The two filled classes pin their presenter fill unconditionally, which outranks the
    // toolkit's own pressed setters — so they did not turn grey under the finger, they did not
    // change at all. The quiet ✕ had no such style, so the toolkit's grey did reach it, and its
    // box went neutral while the mark kept its red. The pseudo-classes are set after the window
    // is shown and the template applied, because the control resets them on attach.
    [AvaloniaTheory]
    [InlineData("accent", "AccentPressedBrush")]
    [InlineData("destructive", "DangerPressedBrush")]
    [InlineData("resultClose", "ChipPressedBrush")]
    public void A_pressed_button_is_a_step_of_its_own_surface(string variant, string pressedBrush)
    {
        var resting = Classed(variant);
        var hovered = Classed(variant);
        var pressed = Classed(variant);
        Show(new Window { Content = new StackPanel { Children = { resting, hovered, pressed } } });
        Dispatcher.UIThread.RunJobs();
        ((IPseudoClasses)hovered.Classes).Set(":pointerover", true);
        ((IPseudoClasses)pressed.Classes).Set(":pressed", true);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Color(Brush(pressedBrush)), Fill(pressed));
        Assert.NotEqual(Fill(resting), Fill(pressed));
        Assert.NotEqual(Fill(hovered), Fill(pressed));
    }

    // This app recedes by a named pair per theme rather than by a fade, so a disabled commit
    // stays readable while clearly leaving the live cyan. Pinned here so nobody converts it to
    // the fade the other apps in this fleet use.
    [AvaloniaFact]
    public void A_disabled_commit_takes_the_app_s_own_disabled_pair()
    {
        var off = Classed("accent");
        off.IsEnabled = false;
        Show(new Window { Content = off });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Color(Brush("AccentDisabledBrush")), Fill(off));
        Assert.Equal(Color(Brush("AccentForegroundDisabledBrush")), Ink(off));
        Assert.Equal(1d, off.Opacity);
    }

    private static Button Classed(string variant)
    {
        var button = new Button { Content = "Save" };
        button.Classes.Add(variant);
        return button;
    }

    private static ContentPresenter Presenter(Button button) =>
        button.GetVisualDescendants().OfType<ContentPresenter>().First();

    private static string Fill(Button button) => Color(Presenter(button).Background);

    private static string Ink(Button button) => Color(Presenter(button).Foreground);

    private static string Color(IBrush? brush) =>
        brush is ISolidColorBrush solid ? solid.Color.ToString() : $"<{brush?.GetType().Name ?? "null"}>";

    private static IBrush Brush(string key)
    {
        var app = Application.Current!;
        return (IBrush)app.FindResource(app.ActualThemeVariant, key)!;
    }

    // Measures the consequence rather than the setter: with auto-hide off the bar takes its own width
    // out of the layout instead of drawing in the band the content's right edge occupies.
    [AvaloniaFact]
    public void A_scroll_bar_takes_its_own_width_instead_of_drawing_over_the_content()
    {
        // No width of its own: it takes the room the viewer leaves, which is the measurement.
        var content = new Border { Height = 400 };
        var viewer = new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var window = Show(new Window { Content = viewer, Width = 200, Height = 120 });
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var bar = viewer.GetVisualDescendants().OfType<ScrollBar>()
            .Single(candidate => candidate.Orientation == Avalonia.Layout.Orientation.Vertical
                && candidate.Bounds.Width > 0);

        Assert.False(viewer.AllowAutoHide);
        Assert.True(viewer.Extent.Height > viewer.Viewport.Height, "the viewer must actually overflow");

        var contentRight = content.TranslatePoint(new Point(content.Bounds.Width, 0), viewer)!.Value.X;
        var barLeft = bar.TranslatePoint(new Point(0, 0), viewer)!.Value.X;
        Assert.True(
            contentRight <= barLeft + 0.5,
            $"the content reaches {contentRight:F0} and the bar starts at {barLeft:F0}, so the bar covers it");
    }

    // The app draws both kinds of bar and Fluent reads a different brush for each. Naming only one key
    // leaves the toolkit's own 20% black on the other, and the keys never appear in the drawing code.
    [AvaloniaTheory]
    [InlineData(false, "ScrollBarThumbBackgroundColor")]
    [InlineData(true, "ScrollBarPanningThumbBackground")]
    public void A_scroll_bar_thumb_paints_the_app_palette_and_not_the_toolkit_default(
        bool floating, string expectedBrush)
    {
        var viewer = new ScrollViewer
        {
            AllowAutoHide = floating,
            Content = new Border { Height = 400 },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var window = Show(new Window { Content = viewer, Width = 200, Height = 120 });
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var thumb = viewer.GetVisualDescendants().OfType<ScrollBar>()
            .Single(candidate => candidate.Orientation == Avalonia.Layout.Orientation.Vertical
                && candidate.Bounds.Width > 0)
            .GetVisualDescendants().OfType<Thumb>().First();

        Assert.Equal(Color(Brush(expectedBrush)), Color(thumb.Background));
    }
}
