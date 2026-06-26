using System.Text.RegularExpressions;

namespace ScriptDock.Services;

/// <summary>
/// Removes ANSI escape sequences (colour, cursor moves, erase) from captured output. The
/// runner stores plain text in v1; faithful in-pane colour is a later refinement.
/// </summary>
public static partial class AnsiStripper
{
    // Matches an ESC-introduced sequence, longest-specific first:
    //   • OSC — ESC ] ... terminated by BEL (0x07) or ST (ESC \). Carries the terminal-title
    //     (ESC]0;…BEL) and hyperlink (ESC]8;;…ST) sequences that oh-my-zsh, git, npm, ls, cargo
    //     emit; without this branch the literal "]0;title" and control bytes leak into the console.
    //   • CSI — ESC [ params intermediates final. Colour, cursor moves, erase.
    //   • Any other ESC sequence — optional intermediates then a final byte: the two-character Fe
    //     escapes (RI, NEL, …) plus charset designators (ESC ( B), keypad (ESC =/>), save/restore
    //     cursor (ESC 7/8) and reset (ESC c).
    // Colour is not yet rendered in-pane (a later refinement); for now every escape is removed.
    [GeneratedRegex("\\x1b(?:\\][^\\x07\\x1b]*(?:\\x07|\\x1b\\\\)|\\[[0-?]*[ -/]*[@-~]|[ -/]*[0-~])")]
    private static partial Regex EscapeSequence();

    public static string Strip(string text) => EscapeSequence().Replace(text, string.Empty);
}
