using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using ScriptDock.Models;

namespace ScriptDock.Converters;

/// <summary>
/// Colours a script row by its state. With parameter <c>bg</c> it returns the background:
/// newly found → soft amber, otherwise transparent. With any other parameter it returns
/// the foreground: removed → red, hidden → silver-gray, otherwise the default text colour.
/// (Brushes are kept here rather than as resources because a converter cannot resolve a
/// <c>StaticResource</c>; the values mirror the app palette.)
/// </summary>
public sealed class ScriptItemColorConverter : IValueConverter
{
    private static readonly IBrush DefaultForeground = new SolidColorBrush(Color.Parse("#1F2430"));
    private static readonly IBrush RemovedForeground = new SolidColorBrush(Color.Parse("#DC2626"));
    private static readonly IBrush HiddenForeground = new SolidColorBrush(Color.Parse("#9AA4B2"));
    private static readonly IBrush NewBackground = new SolidColorBrush(Color.Parse("#FEF3C7"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ScriptItem item)
            return Brushes.Transparent;

        if (string.Equals(parameter as string, "bg", StringComparison.Ordinal))
            return item.Flag == ScriptFlag.New ? NewBackground : Brushes.Transparent;

        if (item.Flag == ScriptFlag.Removed)
            return RemovedForeground;
        if (item.IsHidden)
            return HiddenForeground;
        return DefaultForeground;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
