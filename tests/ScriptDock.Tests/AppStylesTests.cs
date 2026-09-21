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

    // The thumb's colour is the app's, and the key it reads is the one a floating bar uses — which is
    // every region here. The key never appears in the code that draws it, so nothing else would say
    // if it were wrong; the toolkit's own 20% black would simply show through.
    [AvaloniaFact]
    public void The_scroll_bar_thumb_paints_the_app_palette_and_not_the_toolkit_default()
    {
        var viewer = new ScrollViewer
        {
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

        Assert.True(viewer.AllowAutoHide, "the bar should float; nothing here sets it otherwise");
        Assert.Equal(Color(Brush("ScrollBarPanningThumbBackground")), Color(thumb.Background));
    }
}
