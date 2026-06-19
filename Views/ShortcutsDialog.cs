using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ScriptDock.Views;

/// <summary>
/// Keyboard-shortcuts help. Renders the <see cref="ShortcutCatalog"/> it is handed — the same source
/// the live window accelerators are built from — grouped into sections, so the modal can never show a
/// label for a binding that does not exist. Opened from the hamburger menu or via Cmd/Ctrl+/.
/// </summary>
public sealed class ShortcutsDialog : DialogBase
{
    public ShortcutsDialog(IReadOnlyList<ShortcutItem> shortcuts)
    {
        Width = 460;
        Title = "Keyboard Shortcuts";

        var sections = new StackPanel { Spacing = 16 };

        foreach (var group in ShortcutCatalog.GroupOrder)
        {
            var rows = shortcuts.Where(s => s.Group == group).ToList();
            if (rows.Count == 0)
                continue;

            sections.Children.Add(new TextBlock
            {
                Text = ShortcutCatalog.GroupHeader(group),
                FontWeight = FontWeight.SemiBold,
                FontSize = 13,
                Foreground = Palette.Brush("TextSecondaryBrush"),
                Margin = new Thickness(2, 0, 0, 6),
            });
            sections.Children.Add(BuildCard(rows));
        }

        SetContent(sections);
        var buttons = SetButtons(
        [
            new DialogButton("Close", "close", DialogButtonKind.Primary) { IsDefault = true },
        ]);
        SetInitialFocus(buttons["close"]);
    }

    // A rounded card per section holding the section's rows, with a 1px divider between them.
    private Border BuildCard(IReadOnlyList<ShortcutItem> rows)
    {
        var stack = new StackPanel();

        for (var i = 0; i < rows.Count; i++)
        {
            stack.Children.Add(BuildRow(rows[i]));
            if (i < rows.Count - 1)
                stack.Children.Add(new Border { Height = 1, Background = Palette.Brush("BorderBrush") });
        }

        return new Border
        {
            BorderBrush = Palette.Brush("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Background = Palette.Brush("SurfaceBrush"),
            Padding = new Thickness(14, 4),
            Child = stack,
        };
    }

    // Description on the left (wrapping), key on the right.
    private Grid BuildRow(ShortcutItem item)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 18,
            Margin = new Thickness(0, 10),
        };

        var description = new TextBlock
        {
            Text = item.Description,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Palette.Brush("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(description, 0);
        grid.Children.Add(description);

        Control key = item.ShowAsKeycap ? Keycap(item.Label) : PlainAffordance(item.Label);
        Grid.SetColumn(key, 1);
        grid.Children.Add(key);

        return grid;
    }

    // A keycap: a small rounded border with a subtle fill and SemiBold text.
    private Border Keycap(string label) => new()
    {
        Background = Palette.Brush("AppBackgroundBrush"),
        BorderBrush = Palette.Brush("BorderBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(5),
        Padding = new Thickness(8, 3),
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = label,
            FontWeight = FontWeight.SemiBold,
            FontSize = 12,
            Foreground = Palette.Brush("TextPrimaryBrush"),
        },
    };

    // A non-key affordance (e.g. double-click): plain right-aligned text, no keycap box.
    private TextBlock PlainAffordance(string label) => new()
    {
        Text = label,
        FontWeight = FontWeight.SemiBold,
        FontSize = 12,
        Foreground = Palette.Brush("TextSecondaryBrush"),
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center,
    };
}
