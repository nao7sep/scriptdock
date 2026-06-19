using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using ScriptDock.Models;

namespace ScriptDock.Converters;

/// <summary>
/// Colours a script row by its state. With parameter <c>bg</c> it returns the background:
/// newly found → amber tint, otherwise the neutral chip background. With any other parameter it returns the
/// foreground: removed → danger red, hidden → dimmed gray, otherwise the primary text colour.
/// All colours are resolved from the shared <see cref="Palette"/> so the app's dark palette in
/// <c>App.axaml</c> stays the single source of truth — a converter cannot bind a <c>StaticResource</c>.
/// </summary>
public sealed class ScriptItemColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ScriptItem item)
            return Brushes.Transparent;

        if (string.Equals(parameter as string, "bg", StringComparison.Ordinal))
            return Palette.Brush(item.Flag == ScriptFlag.New ? "ScriptNewBackgroundBrush" : "ChipBackgroundBrush");

        if (item.Flag == ScriptFlag.Removed)
            return Palette.Brush("DangerTextBrush");
        if (item.IsHidden)
            return Palette.Brush("ScriptHiddenBrush");
        return Palette.Brush("TextPrimaryBrush");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
