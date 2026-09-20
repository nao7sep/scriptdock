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

    /// <summary>The app's name, which is a brand and so identical in every language.</summary>
    private const string AppName = "ScriptDock";

    public AboutDialog() : this(ExternalLauncher.Open) { }

    internal AboutDialog(System.Func<string, bool> openExternal)
    {
        _openExternal = openExternal;
        Width = 420;
        I18n.Localized.SetTitle(this, "about.title");

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";

        // GitHub is a brand name, so it is the same in every language; the issue tracker is not.
        var githubButton = new Button { Content = ExternalLinkLabel(I18n.Localizer.T("about.github")), Classes = { "tool" } };
        githubButton.Click += (_, _) => OpenExternal(GitHubUrl, I18n.Message.Of("about.openGitHubFailed"));

        var issuesButton = new Button { Content = ExternalLinkLabel(I18n.Localizer.T("about.reportIssue")), Classes = { "tool" } };
        issuesButton.Click += (_, _) => OpenExternal($"{GitHubUrl}/issues", I18n.Message.Of("about.openIssuesFailed"));

        _launchErrorMessage = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        }.Themed(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        var dismissMark = new Shapes.Path
        {
            Width = 10,
            Height = 10,
            StrokeThickness = 1.6,
            StrokeLineCap = PenLineCap.Round,
            Data = Geometry.Parse("M1,1 L9,9 M9,1 L1,9"),
        };
        var dismissLaunchError = new Button
        {
            Classes = { "resultClose" },
            VerticalAlignment = VerticalAlignment.Top,
            Content = dismissMark,
        };
        I18n.Localized.SetAutomationName(dismissLaunchError, "about.closeResult");
        I18n.Localized.SetToolTip(dismissLaunchError, "common.close");
        dismissMark.Bind(
            Shapes.Shape.StrokeProperty,
            new Binding("Foreground") { RelativeSource = new RelativeSource { AncestorType = typeof(Button) } });
        var launchErrorContent = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 8,
            Children = { _launchErrorMessage, dismissLaunchError },
        };
        Grid.SetColumn(dismissLaunchError, 1);
        _launchError = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 7),
            Margin = new Thickness(0, 0, 0, 16),
            IsVisible = false,
            Child = launchErrorContent,
        }
            .Themed(Border.BackgroundProperty, "ErrorSurfaceBrush")
            .Themed(Border.BorderBrushProperty, "DangerTextBrush");
        dismissLaunchError.Click += (_, _) => _launchError.IsVisible = false;
        AutomationProperties.SetLiveSetting(_launchError, AutomationLiveSetting.Assertive);

        var panel = new StackPanel
        {
            Spacing = 0,
            Children =
            {
                new TextBlock { Text = AppName, FontSize = 20, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 4) },
                new TextBlock { Text = I18n.Localizer.T("about.version", ("version", version)), FontSize = 13, Margin = new Thickness(0, 0, 0, 12) }
                    .Themed(TextBlock.ForegroundProperty, "TextSecondaryBrush"),
                new TextBlock
                {
                    Text = I18n.Localizer.T("about.description"),
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
                new TextBlock { Text = I18n.Localizer.T("about.licence"), FontSize = 12 }
                    .Themed(TextBlock.ForegroundProperty, "TextSecondaryBrush"),
            },
        };

        SetContent(panel);
        var buttons = SetButtons([new DialogButton("common.close", "close", DialogButtonKind.Primary) { IsDefault = true }]);
        SetInitialFocus(buttons["close"]);
    }

    public static Task ShowAsync(Window owner) => new AboutDialog().ShowDialog(owner);

    // Each destination carries its own whole sentence rather than a noun dropped into a shared one:
    // a language with articles, cases or particles cannot build that sentence from a fragment.
    private void OpenExternal(string url, I18n.Message failure)
    {
        if (_openExternal(url))
        {
            _launchError.IsVisible = false;
            return;
        }

        _launchErrorMessage.Text = I18n.Localizer.Of(failure);
        _launchError.IsVisible = true;
    }

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
