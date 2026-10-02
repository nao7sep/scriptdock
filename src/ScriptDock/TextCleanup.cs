using System.Text.RegularExpressions;

namespace ScriptDock;

/// <summary>The text-cleanup-conventions' single-line pattern, at its defaults.</summary>
public static partial class TextCleanup
{
    [GeneratedRegex(@"\s*[\r\n]+\s*")]
    private static partial Regex LineBreakRun();

    public static string SingleLine(string text) => LineBreakRun().Replace(text, " ").Trim();
}
