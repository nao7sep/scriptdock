using Avalonia.Controls.Documents;
using Shapes = Avalonia.Controls.Shapes;
using Avalonia.Data;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ScriptDock.Services;

namespace ScriptDock.Views;

public sealed class AboutDialog : DialogBase
{
    private const string GitHubUrl = "https://github.com/nao7sep/scriptdock";
    private readonly System.Func<string, bool> _openExternal;
    private readonly Border _launchError;
    private readonly TextBlock _launchErrorMessage;

    public AboutDialog() : this(ExternalLauncher.Open) { }

    internal AboutDialog(System.Func<string, bool> openExternal)
    {
        _openExternal = openExternal;
        Width = 420;
        Title = "About ScriptDock";

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";

        var githubButton = new Button { Content = ExternalLinkLabel("GitHub"), Classes = { "tool" } };
        githubButton.Click += (_, _) => OpenExternal(GitHubUrl, "GitHub");

        var issuesButton = new Button { Content = ExternalLinkLabel("Report Issue"), Classes = { "tool" } };
        issuesButton.Click += (_, _) => OpenExternal($"{GitHubUrl}/issues", "the issue tracker");

        _launchErrorMessage = new TextBlock
        {
            FontSize = 12,
            Foreground = Palette.Brush("TextPrimaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var launchErrorGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"),
            ColumnSpacing = 7,
            Children =
            {
                ErrorMark(),
                new TextBlock
                {
                    Text = "Error",
                    FontSize = 12,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Palette.Brush("DangerTextBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                },
                _launchErrorMessage,
            },
        };
        Grid.SetColumn(launchErrorGrid.Children[1], 1);
        Grid.SetColumn(launchErrorGrid.Children[2], 2);
        _launchError = new Border
        {
            Background = Palette.Brush("ErrorSurfaceBrush"),
            BorderBrush = Palette.Brush("DangerTextBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 7),
            Margin = new Thickness(0, 0, 0, 16),
            IsVisible = false,
            Child = launchErrorGrid,
        };
        AutomationProperties.SetLiveSetting(_launchError, AutomationLiveSetting.Assertive);

        var panel = new StackPanel
        {
            Spacing = 0,
            Children =
            {
                new TextBlock { Text = "ScriptDock", FontSize = 20, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 4) },
                new TextBlock { Text = $"Version {version}", FontSize = 13, Foreground = Palette.Brush("TextSecondaryBrush"), Margin = new Thickness(0, 0, 0, 12) },
                new TextBlock
                {
                    Text = "Finds the launcher scripts across your repos and runs and reliably restarts them as processes it owns.",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13,
                    Margin = new Thickness(0, 0, 0, 16),
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    Margin = new Thickness(0, 0, 0, 16),
                    Children = { githubButton, issuesButton },
                },
                _launchError,
                new TextBlock { Text = "© 2026 Yoshinao Inoguchi — MIT License", FontSize = 12, Foreground = Palette.Brush("TextSecondaryBrush") },
            },
        };

        SetContent(panel);
        var buttons = SetButtons([new DialogButton("Close", "close", DialogButtonKind.Primary) { IsDefault = true }]);
        SetInitialFocus(buttons["close"]);
    }

    public static Task ShowAsync(Window owner) => new AboutDialog().ShowDialog(owner);

    private void OpenExternal(string url, string destination)
    {
        if (_openExternal(url))
        {
            _launchError.IsVisible = false;
            return;
        }

        _launchErrorMessage.Text = $"Couldn’t open {destination}. Check the log and try again.";
        _launchError.IsVisible = true;
    }

    private static Shapes.Path ErrorMark() => new()
    {
        Width = 14,
        Height = 14,
        Stretch = Stretch.Uniform,
        Stroke = Palette.Brush("DangerTextBrush"),
        StrokeThickness = 1.7,
        StrokeLineCap = PenLineCap.Round,
        StrokeJoin = PenLineJoin.Round,
        Data = Geometry.Parse("M7,1 L13,13 H1 Z M7,5 V8.5 M7,11 V11.1"),
    };

    /// <summary>
    /// A button label with a trailing external-link mark drawn as a vector rather than the
    /// ↗ glyph, whose weight and size vary by font. The mark binds to the button's own
    /// foreground, so it follows theme and hover exactly as the text does.
    ///
    /// It rides INSIDE the text as an inline rather than beside it in a panel, so it is
    /// positioned against the text baseline — the one datum that holds whatever font the
    /// app is set to. Coordinates are written at the target pixel size rather than
    /// stretched, so the stroke keeps one weight, matching the app's XAML icons.
    /// </summary>
    private static Control ExternalLinkLabel(string text)
    {
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        label.Inlines!.Add(new Run(text));
        label.Inlines!.Add(new InlineUIContainer(ExternalLinkMark())
        {
            BaselineAlignment = BaselineAlignment.Baseline,
        });
        return label;
    }

    private static Shapes.Path ExternalLinkMark()
    {
        var mark = new Shapes.Path
        {
            Width = 11,
            Height = 11,
            Margin = new Thickness(5, 0, 0, 0),
            StrokeThickness = 1.3,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            UseLayoutRounding = true,
            Data = Geometry.Parse("M7.8,6.1 V10.35 H0.65 V3.2 H5.0 M6.3,0.65 H10.35 V4.7 M10.35,0.65 L5.2,5.8"),
        };
        mark.Bind(
            Shapes.Shape.StrokeProperty,
            new Binding("Foreground") { RelativeSource = new RelativeSource { AncestorType = typeof(Button) } });
        return mark;
    }

}
