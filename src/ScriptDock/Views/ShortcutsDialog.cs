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
        // One width for every language rather than one per language: sizing to content gives the same
        // surface a different shape in each, and the widest of them (Russian) is what has to fit
        // anyway (modal-dialog conventions). The label-fit tests measure all ten against it.
        Width = 600;
        I18n.Localized.SetTitle(this, "shortcuts.title");

        var sections = new StackPanel { Spacing = 20 };

        foreach (var group in ShortcutCatalog.GroupOrder)
        {
            var rows = shortcuts.Where(s => s.Group == group).ToList();
            if (rows.Count == 0)
                continue;

            // A quiet heading kept close to its rows, with the larger gap between sections.
            sections.Children.Add(new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = I18n.Localizer.T(ShortcutCatalog.GroupHeaderKey(group)),
                        FontWeight = FontWeight.SemiBold,
                        FontSize = 13,
                    }.Themed(TextBlock.ForegroundProperty, "TextSecondaryBrush"),
                    BuildGroup(rows),
                },
            });
        }

        SetContent(sections);
        var buttons = SetButtons(
        [
            new DialogButton("common.close", "close", DialogButtonKind.Primary) { IsDefault = true },
        ]);
        SetInitialFocus(buttons["close"]);
    }

    // A reference list carries no card, no dividers and no zebra (interface-styling conventions): the
    // section heading and the space between rows do the separating, and the keycap is the one mark.
    private StackPanel BuildGroup(IReadOnlyList<ShortcutItem> rows)
    {
        var stack = new StackPanel { Spacing = 10 };
        foreach (var row in rows)
            stack.Children.Add(BuildRow(row));
        return stack;
    }

    // Description on the left (wrapping), key on the right.
    private Grid BuildRow(ShortcutItem item)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 18,
        };

        var description = new TextBlock
        {
            Text = I18n.Localizer.T(item.DescriptionKey),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        }.Themed(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        Grid.SetColumn(description, 0);
        grid.Children.Add(description);

        var keycap = Keycap(item.Label);
        Grid.SetColumn(keycap, 1);
        grid.Children.Add(keycap);

        return grid;
    }

    // A keycap: a small raised rounded box with SemiBold text. Used for every row — including the
    // non-key affordances (e.g. "Double-click / Enter / Space") — so the whole right column is boxed
    // consistently rather than mixing boxed keys with plain affordance text.
    private Border Keycap(string label) => new Border
    {
        Classes = { "keycap" },
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
        }.Themed(TextBlock.ForegroundProperty, "TextPrimaryBrush"),
    }
        .Themed(Border.BackgroundProperty, "SurfaceBrush")
        .Themed(Border.BorderBrushProperty, "ButtonBorderBrush");
}
