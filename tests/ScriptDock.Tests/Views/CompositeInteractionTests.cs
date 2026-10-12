using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ScriptDock.Models;
using ScriptDock.ViewModels;
using ScriptDock.Views;
using SkiaSharp;
using Xunit;

namespace ScriptDock.Tests.Views;

public sealed class CompositeInteractionTests : WindowTest
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Language_popup_starts_at_selection_and_hover_does_not_take_the_keyboard_target(bool keyboard)
    {
        var view = new SettingsView { DataContext = new SettingsDialogViewModel(new AppConfig()) };
        var window = Show(new Window { Content = view, Width = 600, Height = 800 });
        var combo = view.FindControl<ComboBox>("LanguageChoice")!;
        combo.SelectedIndex = 2;
        if (keyboard)
        {
            combo.Focus(NavigationMethod.Tab);
            Press(window, Key.Space);
        }
        else Click(window, combo);
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.True(combo.IsDropDownOpen);
            var selected = (ComboBoxItem)combo.ContainerFromIndex(2)!;
            var earlier = (ComboBoxItem)combo.ContainerFromIndex(0)!;
            var root = TopLevel.GetTopLevel(selected)!;
            Assert.True(selected.IsFocused);
            Press(root, Key.Down);
            var next = (ComboBoxItem)combo.ContainerFromIndex(3)!;
            Assert.True(next.IsFocused);
            root.MouseMove(Center(earlier, root));
            Dispatcher.UIThread.RunJobs();
            Assert.True(earlier.IsPointerOver);
            Assert.True(next.IsFocused);
            Assert.Equal(2, combo.SelectedIndex);
            Press(root, Key.Down);
            Assert.True(combo.ContainerFromIndex(4)!.IsFocused);
            Press(root, Key.Enter);
            Assert.Equal(4, combo.SelectedIndex);
            Assert.False(combo.IsDropDownOpen);

            combo.IsDropDownOpen = true;
            Dispatcher.UIThread.RunJobs();
            Click(TopLevel.GetTopLevel(combo.ContainerFromIndex(0)!)!, combo.ContainerFromIndex(0)!);
            Assert.Equal(0, combo.SelectedIndex);
            Assert.False(combo.IsDropDownOpen);
        }
        finally { combo.IsDropDownOpen = false; }
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void A_list_keeps_selection_and_keyboard_focus_when_an_earlier_item_is_hovered(string theme)
    {
        var list = new ListBox { ItemsSource = new[] { "first", "second", "third", "fourth" } };
        var window = Show(new Window { Content = list, Width = 300, Height = 240,
            RequestedThemeVariant = theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark });
        list.SelectedIndex = 0;
        list.ContainerFromIndex(0)!.Focus(NavigationMethod.Tab);
        Press(window, Key.Down);
        Press(window, Key.Down);
        Assert.Equal(2, list.SelectedIndex);
        var target = list.ContainerFromIndex(2)!;
        var presenter = target.GetVisualDescendants().OfType<ContentPresenter>().First();
        var ring = presenter.BoxShadow;
        Assert.True(ring.Count > 0);
        var resting = RenderedBackground(window, presenter);
        window.MouseMove(Center(list.ContainerFromIndex(0)!, window));
        Dispatcher.UIThread.RunJobs();
        Assert.True(list.ContainerFromIndex(0)!.IsPointerOver);
        Assert.Equal(2, list.SelectedIndex);
        Assert.True(target.IsFocused);
        Assert.Equal(resting, RenderedBackground(window, presenter));
        window.MouseMove(Center(target, window));
        Dispatcher.UIThread.RunJobs();
        Assert.True(target.IsFocused);
        Assert.Equal(2, list.SelectedIndex);
        Assert.Equal(ring, presenter.BoxShadow);
        Assert.NotEqual(resting, RenderedBackground(window, presenter, $"list-{theme.ToLowerInvariant()}-combined.png"));
        Press(window, Key.Down);
        Assert.Equal(3, list.SelectedIndex);
        Click(window, list.ContainerFromIndex(1)!);
        Assert.Equal(1, list.SelectedIndex);
    }

    [AvaloniaTheory]
    [InlineData("Light", false, false)]
    [InlineData("Dark", false, false)]
    [InlineData("Light", true, false)]
    [InlineData("Dark", true, false)]
    [InlineData("Light", false, true)]
    [InlineData("Dark", false, true)]
    public void A_selected_script_chip_keeps_selection_and_focus_while_hover_remains_visible(string theme, bool hidden, bool removed)
    {
        var variant = theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        var window = Show(new MainWindow { RequestedThemeVariant = variant });
        var list = window.FindControl<ListBox>("ScriptsList")!;
        list.ItemsSource = new[]
        {
            new ScriptItem("/owned/first.command") { DisplayName = "First script", IsHidden = hidden, Flag = removed ? ScriptFlag.Removed : ScriptFlag.None },
            new ScriptItem("/owned/second.command") { DisplayName = "Second script" },
        };
        Dispatcher.UIThread.RunJobs();
        list.SelectedIndex = 1;
        list.ContainerFromIndex(1)!.Focus(NavigationMethod.Tab);
        Press(window, Key.Left);
        Assert.Equal(0, list.SelectedIndex);
        var selected = (ListBoxItem)list.ContainerFromIndex(0)!;
        Dispatcher.UIThread.RunJobs();
        var chip = selected.GetVisualDescendants().OfType<Border>().Single(border => border.Classes.Contains("chip"));
        var presenter = selected.GetVisualDescendants().OfType<ContentPresenter>().First();
        var resting = Color(chip.Background);
        var selection = Color(chip.BorderBrush);
        var ring = presenter.BoxShadow;
        Assert.True(ring.Count > 0);
        Assert.True(selected.IsFocused);
        var restingPixel = RenderedBackground(window, chip);

        window.MouseMove(Center(chip, window));
        Dispatcher.UIThread.RunJobs();
        Assert.True(selected.IsPointerOver);
        Assert.Equal(0, list.SelectedIndex);
        Assert.True(selected.IsFocused);
        Assert.NotEqual(resting, Color(chip.Background));
        Assert.Equal(selection, Color(chip.BorderBrush));
        Assert.Equal(ring, presenter.BoxShadow);
        var hoveredPixel = RenderedBackground(window, chip, $"scripts-{theme.ToLowerInvariant()}-{(hidden ? "hidden" : removed ? "removed" : "ordinary")}.png");
        Assert.NotEqual(restingPixel, hoveredPixel);
    }

    // Read actual Skia pixels between the text and right edge; property values alone cannot prove
    // that the state treatment survives the control template and renderer.
    internal static SKColor RenderedBackground(TopLevel root, Control surface, string? fileName = null)
    {
        using var frame = root.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var png = new MemoryStream();
        frame.Save(png, PngBitmapEncoderOptions.Default);
        png.Position = 0;
        using var pixels = SKBitmap.Decode(png);
        var point = surface.TranslatePoint(new Point(surface.Bounds.Width - 5, surface.Bounds.Height / 2), root)!.Value;
        var color = pixels.GetPixel((int)(point.X * root.RenderScaling), (int)(point.Y * root.RenderScaling));
        if (fileName is not null)
        {
            var directory = Path.Combine(RepoRoot(), "TestResults", "focus-states");
            Directory.CreateDirectory(directory);
            frame.Save(Path.Combine(directory, fileName), PngBitmapEncoderOptions.Default);
        }
        return color;
    }

    private static string Color(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color.ToString();
    private static Point Center(Control control, Visual root) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;
    private static void Click(TopLevel root, Control control)
    {
        var point = Center(control, root);
        root.MouseMove(point);
        root.MouseDown(point, MouseButton.Left);
        root.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }
    private static void Press(TopLevel root, Key key)
    {
        root.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        root.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "ScriptDock")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
