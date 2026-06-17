using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;

namespace ScriptDock.Views;

/// <summary>Keyboard-shortcuts help, opened from the hamburger menu.</summary>
public sealed class ShortcutsDialog : DialogBase
{
    private static readonly (string Key, string Description)[] Shortcuts =
    [
        ("Double-click / Enter / Space", "Run the selected script — or restart it if it is already running"),
        ("⌘ R", "Rescan the root directories"),
        ("Delete", "On a Recent item: stop it if running, otherwise dismiss it"),
        ("↑ / ↓", "Move the selection within a list"),
        ("☰ menu", "Settings, Keyboard Shortcuts, About, Reveal Logs"),
    ];

    public ShortcutsDialog()
    {
        Width = 460;
        Title = "Keyboard Shortcuts";

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            RowSpacing = 10,
            ColumnSpacing = 18,
        };

        for (var row = 0; row < Shortcuts.Length; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var (key, description) = Shortcuts[row];

            var keyBlock = new TextBlock { Text = key, FontWeight = FontWeight.SemiBold };
            Grid.SetRow(keyBlock, row);
            Grid.SetColumn(keyBlock, 0);

            var descBlock = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray };
            Grid.SetRow(descBlock, row);
            Grid.SetColumn(descBlock, 1);

            grid.Children.Add(keyBlock);
            grid.Children.Add(descBlock);
        }

        SetContent(grid);
        var buttons = SetButtons([new DialogButton("Close", "close", DialogButtonKind.Primary) { IsDefault = true }]);
        SetInitialFocus(buttons["close"]);
    }

    public static Task ShowAsync(Window owner) => new ShortcutsDialog().ShowDialog(owner);
}
