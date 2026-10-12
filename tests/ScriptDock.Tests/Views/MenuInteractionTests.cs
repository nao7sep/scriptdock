using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ScriptDock.Views;
using Xunit;

namespace ScriptDock.Tests.Views;

public sealed class MenuInteractionTests : WindowTest
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Opening_prepares_the_first_target_and_the_first_arrow_moves_from_it(bool keyboard)
    {
        var scene = Open(keyboard);
        try
        {
            Assert.True(scene.Items[0].IsFocused);
            Assert.Same(scene.Items[0], scene.Presenter.SelectedItem);
            Press(scene.Root, Key.Down);
            Assert.True(scene.Items[1].IsFocused);
            Assert.Same(scene.Items[1], scene.Presenter.SelectedItem);
        }
        finally { scene.Flyout.Hide(); }
    }

    [AvaloniaFact]
    public void Hover_does_not_retarget_arrows_or_Enter_and_click_acts_on_its_item()
    {
        var scene = Open(true);
        var clicked = -1;
        for (var index = 0; index < scene.Items.Length; index++)
        {
            var target = index;
            scene.Items[index].AddHandler(MenuItem.ClickEvent, (_, e) =>
            {
                clicked = target;
                e.Handled = true;
            });
        }
        try
        {
            // Establish a target through normal keyboard navigation, then cross an earlier item.
            scene.Items[0].Focus(NavigationMethod.Directional);
            Press(scene.Root, Key.Down);
            Press(scene.Root, Key.Down);
            Assert.True(scene.Items[2].IsFocused);
            scene.Root.MouseMove(Center(scene.Items[0], scene.Root));
            Dispatcher.UIThread.RunJobs();
            Assert.True(scene.Items[0].IsPointerOver);
            Assert.True(scene.Items[2].IsFocused);
            Assert.Same(scene.Items[2], scene.Presenter.SelectedItem);
            Press(scene.Root, Key.Down);
            Assert.True(scene.Items[3].IsFocused);
            Press(scene.Root, Key.Up);
            Press(scene.Root, Key.Up);
            Assert.True(scene.Items[1].IsFocused);
            scene.Root.MouseMove(Center(scene.Items[0], scene.Root));
            Press(scene.Root, Key.Enter);
            // Settings/Records have no effects in this view-only fixture (no VM or Records host).
            Assert.Equal(1, clicked);
            Assert.False(scene.Flyout.IsOpen);

            scene.Flyout.ShowAt(scene.Button);
            Dispatcher.UIThread.RunJobs();
            var root = TopLevel.GetTopLevel(scene.Items[0])!;
            scene.Items[3].Focus(NavigationMethod.Directional);
            var point = Center(scene.Items[0], root);
            root.MouseMove(point);
            root.MouseDown(point, MouseButton.Left);
            root.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, clicked);
            Assert.False(scene.Flyout.IsOpen);
        }
        finally { scene.Flyout.Hide(); }
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Hover_and_keyboard_target_have_distinct_rendered_marks_in_both_themes(string theme)
    {
        var scene = Open(true, theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark);
        try
        {
            Press(scene.Root, Key.Down);
            Press(scene.Root, Key.Down);
            var target = scene.Items[2];
            var earlier = scene.Items[0];
            var targetFill = LayoutRoot(target).Background;
            var restingFill = LayoutRoot(earlier).Background;
            var resting = CompositeInteractionTests.RenderedBackground(scene.Root, LayoutRoot(earlier));
            var targeted = CompositeInteractionTests.RenderedBackground(scene.Root, LayoutRoot(target));
            Assert.NotEqual(resting, targeted);

            scene.Root.MouseMove(Center(earlier, scene.Root));
            Dispatcher.UIThread.RunJobs();
            Assert.True(target.IsFocused);
            Assert.True(target.IsSelected);
            Assert.False(earlier.IsSelected);
            Assert.Equal(targetFill, LayoutRoot(target).Background);
            Assert.NotEqual(restingFill, LayoutRoot(earlier).Background);
            var hovered = CompositeInteractionTests.RenderedBackground(scene.Root, LayoutRoot(earlier), $"menu-{theme.ToLowerInvariant()}-separate.png");
            Assert.NotEqual(resting, hovered);
            Assert.NotEqual(targeted, hovered);
            Assert.Equal(targeted, CompositeInteractionTests.RenderedBackground(scene.Root, LayoutRoot(target)));

            scene.Root.MouseMove(Center(target, scene.Root));
            Dispatcher.UIThread.RunJobs();
            Assert.True(target.IsSelected);
            Assert.True(target.IsFocused);
            Assert.NotEqual(targeted, CompositeInteractionTests.RenderedBackground(scene.Root, LayoutRoot(target), $"menu-{theme.ToLowerInvariant()}-combined.png"));

            // Exiting the targeted item also must not clear native selection or focus.
            scene.Root.MouseMove(Center(scene.Items[1], scene.Root));
            Dispatcher.UIThread.RunJobs();
            Assert.Same(target, scene.Presenter.SelectedItem);
            Assert.True(target.IsFocused);
        }
        finally { scene.Flyout.Hide(); }
    }

    private static Border LayoutRoot(MenuItem item) => item.GetVisualDescendants().OfType<Border>()
        .Single(border => border.Name == "PART_LayoutRoot");

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void A_pressed_command_remains_visually_distinct_from_hover(string theme)
    {
        var scene = Open(true, theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark);
        try
        {
            Press(scene.Root, Key.Down);
            var item = scene.Items[0]; // Settings has no outbound effect without a view model.
            var point = Center(item, scene.Root);
            scene.Root.MouseMove(point);
            var hovered = CompositeInteractionTests.RenderedBackground(scene.Root, LayoutRoot(item));
            scene.Root.MouseDown(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(":pressed", item.Classes);
            Assert.NotEqual(hovered, CompositeInteractionTests.RenderedBackground(scene.Root, LayoutRoot(item)));
            scene.Root.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.False(scene.Flyout.IsOpen);
        }
        finally { scene.Flyout.Hide(); }
    }

    private Scene Open(bool keyboard, ThemeVariant? theme = null)
    {
        var window = Show(new MainWindow { RequestedThemeVariant = theme ?? ThemeVariant.Default });
        var button = window.GetVisualDescendants().OfType<Button>()
            .Single(candidate => candidate.Flyout is MenuFlyout);
        var flyout = (MenuFlyout)button.Flyout!;
        if (keyboard)
        {
            button.Focus(NavigationMethod.Tab);
            Press(window, Key.Space);
        }
        else
        {
            var point = Center(button, window);
            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.True(flyout.IsOpen);
        var items = flyout.Items.Cast<MenuItem>().ToArray();
        return new Scene(button, flyout, items,
            items[0].FindAncestorOfType<MenuFlyoutPresenter>()!, TopLevel.GetTopLevel(items[0])!);
    }

    private static Point Center(Control control, Visual root) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;

    private static void Press(TopLevel root, Key key)
    {
        root.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        root.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    private sealed record Scene(Button Button, MenuFlyout Flyout, MenuItem[] Items,
        MenuFlyoutPresenter Presenter, TopLevel Root);
}
