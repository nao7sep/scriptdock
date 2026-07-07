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
        // Size to content rather than a guessed fixed width: the dialog ends up exactly as wide as
        // its widest row needs (description + keycap on one line). MaxWidth caps it so an unusually
        // long future label wraps instead of producing an over-wide window. (DialogBase already sizes
        // the height to content; this adds the width dimension.)
        SizeToContent = Avalonia.Controls.SizeToContent.WidthAndHeight;
        MaxWidth = 720;
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

        var keycap = Keycap(item.Label);
        Grid.SetColumn(keycap, 1);
        grid.Children.Add(keycap);

        return grid;
    }

    // A keycap: a small recessed rounded box with a subtle fill and SemiBold text, matching the
    // other fleet shortcut modals (DayNote, ZipKit). Used for every row — including the non-key
    // affordances (e.g. "Double-click / Enter / Space") — so the whole right column is boxed
    // consistently rather than mixing boxed keys with plain affordance text.
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
}
