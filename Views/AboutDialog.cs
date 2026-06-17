using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ScriptDock.Services;

namespace ScriptDock.Views;

public sealed class AboutDialog : DialogBase
{
    private const string GitHubUrl = "https://github.com/nao7sep/scriptdock";

    public AboutDialog()
    {
        Width = 420;
        Title = "About ScriptDock";

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";

        var githubButton = new Button { Content = "GitHub ↗", Classes = { "tool" } };
        githubButton.Click += (_, _) => ExternalLauncher.Open(GitHubUrl);

        var issuesButton = new Button { Content = "Report Issue ↗", Classes = { "tool" } };
        issuesButton.Click += (_, _) => ExternalLauncher.Open($"{GitHubUrl}/issues");

        var panel = new StackPanel
        {
            Spacing = 0,
            Children =
            {
                new TextBlock { Text = "ScriptDock", FontSize = 20, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 4) },
                new TextBlock { Text = $"Version {version}", FontSize = 13, Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 12) },
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
                new TextBlock { Text = "© 2026 Yoshinao Inoguchi — MIT License", FontSize = 12, Foreground = Brushes.Gray },
            },
        };

        SetContent(panel);
        var buttons = SetButtons([new DialogButton("Close", "close", DialogButtonKind.Primary) { IsDefault = true }]);
        SetInitialFocus(buttons["close"]);
    }

    public static Task ShowAsync(Window owner) => new AboutDialog().ShowDialog(owner);
}
