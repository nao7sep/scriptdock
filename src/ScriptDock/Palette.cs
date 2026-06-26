using Avalonia;
using Avalonia.Media;

namespace ScriptDock;

/// <summary>
/// Resolves a named brush from the application palette declared in <c>App.axaml</c> — the single
/// source of truth for ScriptDock's colours. Used by code that cannot reference a XAML
/// <c>StaticResource</c> directly: the script-tile colour converter and the code-built dialogs.
/// Falls back to transparent if a key is missing, so a typo surfaces as a visible gap rather than
/// a crash.
/// </summary>
public static class Palette
{
    public static IBrush Brush(string key) =>
        Application.Current is { } app
        && app.Resources.TryGetResource(key, null, out var value)
        && value is IBrush brush
            ? brush
            : Brushes.Transparent;
}
