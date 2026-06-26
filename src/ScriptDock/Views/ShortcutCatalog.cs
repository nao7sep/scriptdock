using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace ScriptDock.Views;

/// <summary>Semantic section a shortcut belongs to; drives the modal's section order and headers.</summary>
public enum ShortcutGroup
{
    Commands,
    Focus,
    Scripts,
    Recent,
    Navigation,
}

/// <summary>
/// Identifies a window-level command accelerator. The window maps each value to the matching behavior
/// in <c>MainWindow.TryRunShortcut</c>; display-only rows (Run, Stop/Dismiss, Up/Down) carry no action.
/// </summary>
public enum ShortcutAction
{
    Rescan,
    ToggleShowHidden,
    OpenSettings,
    ShowShortcuts,
    FocusScripts,
    FocusRecent,
    FocusConsole,
}

/// <summary>
/// One row of the shortcut catalog. <see cref="Gesture"/> and <see cref="Action"/> are set only for
/// window-level command accelerators, which the window both binds and dispatches; display-only rows
/// describe behavior owned by a control and carry just the label.
/// </summary>
public sealed record ShortcutItem(
    ShortcutGroup Group,
    string Description,
    string Label,
    KeyGesture? Gesture = null,
    ShortcutAction? Action = null);

/// <summary>
/// The single source of truth for ScriptDock's keyboard shortcuts. Both the live window accelerators
/// and the help modal are derived from one ordered list, so a displayed label can never describe a
/// binding that does not exist. The catalog owns presentation (labels, grouping) and the gesture
/// derivation; it holds no command logic — the window maps each <see cref="ShortcutAction"/> to a command.
/// </summary>
public static class ShortcutCatalog
{
    /// <summary>Section order for the help modal; only non-empty groups render.</summary>
    public static readonly IReadOnlyList<ShortcutGroup> GroupOrder =
    [
        ShortcutGroup.Commands,
        ShortcutGroup.Focus,
        ShortcutGroup.Scripts,
        ShortcutGroup.Recent,
        ShortcutGroup.Navigation,
    ];

    public static string GroupHeader(ShortcutGroup group) => group switch
    {
        ShortcutGroup.Commands => "Commands",
        ShortcutGroup.Focus => "Focus",
        ShortcutGroup.Scripts => "Scripts",
        ShortcutGroup.Recent => "Recent",
        ShortcutGroup.Navigation => "Navigation",
        _ => group.ToString(),
    };

    /// <summary>
    /// The platform command key — <c>Meta</c> (Cmd) on macOS, <c>Control</c> on Windows/Linux. Resolved
    /// once here from the framework's own notion of the command modifier, so every accelerator binds the
    /// right modifier on every platform while the labels stay the universal <c>Cmd/Ctrl+…</c>.
    /// </summary>
    public static KeyModifiers CommandModifier(TopLevel top) =>
        top.GetPlatformSettings()?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;

    public static IReadOnlyList<ShortcutItem> Build(TopLevel top)
    {
        var cmd = CommandModifier(top);
        return new List<ShortcutItem>
        {
            // Commands — global app accelerators, in rough order of use.
            Command(ShortcutGroup.Commands, "Rescan", cmd, Key.R, "R", ShortcutAction.Rescan),
            Command(ShortcutGroup.Commands, "Toggle hidden scripts", cmd | KeyModifiers.Shift, Key.H, "Shift+H", ShortcutAction.ToggleShowHidden),
            Command(ShortcutGroup.Commands, "Settings", cmd, Key.OemComma, "Comma", ShortcutAction.OpenSettings),
            Command(ShortcutGroup.Commands, "Keyboard shortcuts", cmd, Key.OemQuestion, "Slash", ShortcutAction.ShowShortcuts),

            // Focus — jump to a pane without the mouse (numbered to match the layout).
            Command(ShortcutGroup.Focus, "Scripts list", cmd, Key.D1, "1", ShortcutAction.FocusScripts),
            Command(ShortcutGroup.Focus, "Recent list", cmd, Key.D2, "2", ShortcutAction.FocusRecent),
            Command(ShortcutGroup.Focus, "Console input", cmd, Key.D3, "3", ShortcutAction.FocusConsole),

            // Scripts — running the selection is owned by the tile (pointer + keys), listed for discoverability.
            Display(ShortcutGroup.Scripts, "Run or restart the selected script", "Double-click / Enter / Space"),

            // Recent — Delete is owned by the Recent list while it has focus.
            Display(ShortcutGroup.Recent, "Stop or dismiss the selected run", "Delete"),

            // Navigation — native list selection.
            Display(ShortcutGroup.Navigation, "Move between items in the focused list", "Up / Down"),
        };
    }

    /// <summary>
    /// Builds a command accelerator from one definition so the label and the gesture cannot diverge.
    /// The label is always the universal <c>Cmd/Ctrl+…</c>; only the gesture's modifier is platform-resolved.
    /// </summary>
    private static ShortcutItem Command(
        ShortcutGroup group, string description, KeyModifiers cmd, Key key, string keyName, ShortcutAction action) =>
        new(group, description, "Cmd/Ctrl+" + keyName, new KeyGesture(key, cmd), action);

    private static ShortcutItem Display(ShortcutGroup group, string description, string label) =>
        new(group, description, label);
}
