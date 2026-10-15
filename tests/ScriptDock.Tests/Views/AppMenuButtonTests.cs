using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ScriptDock.Views;
using Xunit;

namespace ScriptDock.Tests.Views;

/// <summary>
/// The hamburger is a ghost button, as across the fleet: a bare mark with no border or fill at rest, the
/// button's hover fill under the pointer and while its menu is open, and its pressed fill on press.
/// </summary>
public sealed class AppMenuButtonTests : WindowTest
{
    private static Color? Fill(ContentPresenter presenter) => (presenter.Background as ISolidColorBrush)?.Color;

    // The brush the window's theme gives the key, read from the theme dictionaries.
    private static Color Resource(Control control, string key) =>
        control.TryFindResource(key, control.ActualThemeVariant, out var value) && value is ISolidColorBrush brush
            ? brush.Color
            : throw new Xunit.Sdk.XunitException($"No brush for {key}");

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void It_has_no_border_or_fill_at_rest_and_a_fill_on_hover_press_and_while_open(string theme)
    {
        var window = Show(new MainWindow { RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light });
        var button = window.FindControl<Button>("AppMenuButton")!;
        var presenter = button.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
        var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;

        Assert.Equal(default, button.BorderThickness);
        Assert.Equal(Colors.Transparent, Fill(presenter));

        window.MouseMove(center);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Resource(button, "ButtonHoverBrush"), Fill(presenter));

        window.MouseDown(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Resource(button, "ButtonPressedBrush"), Fill(presenter));

        window.MouseUp(center, MouseButton.Left);
        window.MouseMove(new Point(5, window.Bounds.Height - 5)); // the pointer leaves; the menu stays open
        Dispatcher.UIThread.RunJobs();
        var flyout = (MenuFlyout)button.Flyout!;
        try
        {
            Assert.True(flyout.IsOpen);
            Assert.Equal(Resource(button, "ButtonHoverBrush"), Fill(presenter));
        }
        finally
        {
            flyout.Hide();
            Dispatcher.UIThread.RunJobs();
        }
        Assert.Equal(Colors.Transparent, Fill(presenter));
    }
}
