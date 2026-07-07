using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using ScriptDock.Models;

namespace ScriptDock.Converters;

/// <summary>
/// The foreground brush for a script row by its state: removed → danger red, hidden → dimmed
/// gray, otherwise the primary text colour. (A newly-found script is flagged by an accent dot in
/// the tile, not by a colour here, and the chip background is owned by the tile styles.) Resolved
/// from the shared <see cref="Palette"/> so <c>App.axaml</c> stays the single source of truth — a
/// converter cannot bind a <c>StaticResource</c>.
/// </summary>
public sealed class ScriptItemColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ScriptItem item ? Palette.Brush(PaletteKeyFor(item)) : Brushes.Transparent;

    /// <summary>
    /// The palette key for a row's foreground by state — removed → danger, else hidden → dimmed,
    /// else primary text. This is the converter's actual decision (precedence: removed over hidden);
    /// it is split from the brush lookup so it can be unit-tested without a running Application, which
    /// <see cref="Palette.Brush"/> needs to resolve a key into a brush.
    /// </summary>
    internal static string PaletteKeyFor(ScriptItem item) =>
        item.Flag == ScriptFlag.Removed ? "DangerTextBrush"
        : item.IsHidden ? "ScriptHiddenBrush"
        : "TextPrimaryBrush";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
