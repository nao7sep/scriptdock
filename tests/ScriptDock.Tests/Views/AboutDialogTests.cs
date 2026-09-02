using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ScriptDock.Views;
using Xunit;

namespace ScriptDock.Tests.Views;

public sealed class AboutDialogTests
{
    [AvaloniaFact]
    public void External_launch_failure_stays_in_the_about_dialog_and_preserves_reachable_actions()
    {
        var dialog = new AboutDialog(_ => false);
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        var before = dialog.Bounds.Height;
        var externalButton = dialog.GetVisualDescendants()
            .OfType<Button>()
            .First(button => button.Classes.Contains("tool"));

        externalButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var result = dialog.GetVisualDescendants()
            .OfType<Border>()
            .Single(border => AutomationProperties.GetLiveSetting(border) == AutomationLiveSetting.Assertive);
        var close = dialog.GetVisualDescendants()
            .OfType<Button>()
            .Single(button => Equals(button.Content, "Close"));
        var dismiss = result.GetVisualDescendants()
            .OfType<Button>()
            .Single(button => AutomationProperties.GetName(button) == "Close result");
        Assert.True(result.IsVisible);
        Assert.True(dialog.Bounds.Height > before);
        Assert.True(close.IsVisible);
        Assert.True(close.Bounds.Height > 0);
        var closeBottom = close.TranslatePoint(new Point(close.Bounds.Width, close.Bounds.Height), dialog);
        Assert.NotNull(closeBottom);
        Assert.True(closeBottom.Value.Y <= dialog.ClientSize.Height);
        Assert.True(dismiss.Bounds.Height > 0);
        Assert.IsType<Avalonia.Controls.Shapes.Path>(dismiss.Content);

        dismiss.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(result.IsVisible);
    }
}
