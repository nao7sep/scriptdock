using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ScriptDock.Controls;

namespace ScriptDock.Views;

/// <summary>
/// Cmd/Ctrl+W closes the window it is pressed in through that window's own close path, as its close
/// button does, so the main window runs its quit flow and a dialog its discard or busy guard
/// (keyboard-shortcut-conventions, Closing a window). Every window the app opens attaches it. On macOS the
/// Window menu's Close takes the chord first and sends the same close; this binding is the Windows path
/// and the fallback where that menu is absent.
/// </summary>
internal static class CloseWindowShortcut
{
    public static void Attach(Window window) =>
        window.AddHandler(InputElement.KeyDownEvent, (_, e) => OnKeyDown(window, e), RoutingStrategies.Bubble);

    internal static bool Matches(KeyEventArgs e, KeyModifiers command) =>
        e.Key == Key.W && e.KeyModifiers == command;

    private static void OnKeyDown(Window window, KeyEventArgs e)
    {
        // A chord pressed mid-composition belongs to the pending candidate (text-input-ime).
        if (e.Handled || !Matches(e, ShortcutCatalog.CommandModifier(window)) || ComposingTextBox.IsFocusedElementComposing(window))
            return;

        e.Handled = true;
        window.Close();
    }
}
