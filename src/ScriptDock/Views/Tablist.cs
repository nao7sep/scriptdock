using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ScriptDock.Views;

/// <summary>
/// A tab strip as a tablist (composite-control conventions, Tablist): one tab stop, on the selected tab;
/// Left and Right move the selection without wrapping, Home and End go to the first and last tab, and the
/// selection follows focus, since swapping a panel is a cheap local change. The strip's own selection is
/// the one value; focus and the tab stop follow it.
/// </summary>
internal static class Tablist
{
    public static void Attach(TabStrip strip)
    {
        KeyboardNavigation.SetTabNavigation(strip, KeyboardNavigationMode.Once);
        strip.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        strip.SelectionChanged += (_, _) => KeepTabStop(strip);
        strip.Loaded += (_, _) => KeepTabStop(strip);
    }

    // Tab enters the strip on the selected tab, wherever focus last was in it.
    private static void KeepTabStop(TabStrip strip)
    {
        if (strip.ContainerFromIndex(strip.SelectedIndex) is { } selected)
            KeyboardNavigation.SetTabOnceActiveElement(strip, selected);
    }

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TabStrip strip || e.KeyModifiers != KeyModifiers.None)
            return;
        var last = strip.ItemCount - 1;
        int? next = e.Key switch
        {
            Key.Left => System.Math.Max(strip.SelectedIndex - 1, 0),
            Key.Right => System.Math.Min(strip.SelectedIndex + 1, last),
            Key.Home => 0,
            Key.End => last,
            _ => null,
        };
        if (next is not { } index)
            return;
        e.Handled = true;
        strip.SelectedIndex = index;
        strip.ContainerFromIndex(index)?.Focus(NavigationMethod.Directional);
    }
}
