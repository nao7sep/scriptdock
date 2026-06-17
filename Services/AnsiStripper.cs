using System.Text.RegularExpressions;

namespace ScriptDock.Services;

/// <summary>
/// Removes ANSI escape sequences (colour, cursor moves, erase) from captured output. The
/// runner stores plain text in v1; faithful in-pane colour is a later refinement.
/// </summary>
public static partial class AnsiStripper
{
    // ESC, then either a two-character escape (ESC + one Fe byte) or a CSI sequence
    // (ESC [ params intermediates final).
    [GeneratedRegex("\\x1b(?:[@-Z\\-_]|\\[[0-?]*[ -/]*[@-~])")]
    private static partial Regex EscapeSequence();

    public static string Strip(string text) => EscapeSequence().Replace(text, string.Empty);
}
