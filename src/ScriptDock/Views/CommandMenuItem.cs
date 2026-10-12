using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ScriptDock.Views;

/// <summary>
/// The application's flat command menu keeps hover separate from its keyboard target.
/// Handle only the bubbling item-hover events before Avalonia's menu handler selects or
/// clears an item; native pointer-over styling, keyboard navigation and clicks still run.
/// </summary>
public sealed class CommandMenuItem : MenuItem
{
    protected override Type StyleKeyOverride => typeof(MenuItem);

    public CommandMenuItem()
    {
        Classes.Add("commandMenuItem");
        AddHandler(PointerEnteredItemEvent, KeepKeyboardTarget);
        AddHandler(PointerExitedItemEvent, KeepKeyboardTarget);
    }

    private static void KeepKeyboardTarget(object? sender, RoutedEventArgs e) => e.Handled = true;
}
