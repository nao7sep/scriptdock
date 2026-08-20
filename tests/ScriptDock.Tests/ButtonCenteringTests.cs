using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Xunit;

namespace ScriptDock.Tests;

/// <summary>
/// A button's label must sit centred in its box. This is not a matter of taste that a
/// reviewer could catch by reading styles: Avalonia Fluent's stock button padding is
/// 8,5,8,6 — a pixel taller at the bottom — so any button that does not override
/// Padding renders its label a pixel high, identically in every app using the theme.
/// The app sets a symmetric default; this measures that it took effect, across the
/// button kinds and for labels with and without descenders.
/// </summary>
public sealed class ButtonCenteringTests
{
    private static (double Top, double Bottom) Gaps(Button button)
    {
        var root = new Window { Width = 300, Height = 120, Content = button };
        root.Show();
        root.Measure(new Size(300, 120));
        root.Arrange(new Rect(0, 0, 300, 120));
        root.UpdateLayout();

        var text = button.GetVisualDescendants().OfType<TextBlock>().First();
        var top = text.TranslatePoint(new Point(0, 0), button)!.Value.Y;
        return (top, button.Bounds.Height - (top + text.Bounds.Height));
    }

    [AvaloniaTheory]
    [InlineData("Save", null)]
    [InlineData("Save", "accent")]
    [InlineData("Save", "tool")]
    [InlineData("Save", "destructive")]
    [InlineData("gyp", "tool")]        // descenders only
    [InlineData("Save gyp", null)]     // caps and descenders together
    public void LabelIsVerticallyCentred(string label, string? cssClass)
    {
        var button = new Button { Content = label };
        if (cssClass is not null) button.Classes.Add(cssClass);

        var (top, bottom) = Gaps(button);
        Assert.True(
            System.Math.Abs(top - bottom) < 0.51,
            $"'{label}' (class {cssClass ?? "none"}) sits off centre: top {top:F2}px, bottom {bottom:F2}px");
    }
}
