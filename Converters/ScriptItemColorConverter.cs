using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using ScriptDock.Models;

namespace ScriptDock.Converters;

/// <summary>
/// The foreground brush for a script row by its state: removed → danger red, hidden → dimmed
/// gray, otherwise the primary text colour. (A newly-found script is flagged by an accent dot in
/// the tile, not by a colour here, and the chip background is a single neutral token.) Resolved
/// from the shared <see cref="Palette"/> so <c>App.axaml</c> stays the single source of truth — a
/// converter cannot bind a <c>StaticResource</c>.
/// </summary>
public sealed class ScriptItemColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ScriptItem item)
            return Brushes.Transparent;

        if (item.Flag == ScriptFlag.Removed)
            return Palette.Brush("DangerTextBrush");
        if (item.IsHidden)
            return Palette.Brush("ScriptHiddenBrush");
        return Palette.Brush("TextPrimaryBrush");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
