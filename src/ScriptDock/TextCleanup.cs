using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ScriptDock;

/// <summary>The text-cleanup-conventions' single-line and multiline-truncation patterns, at their defaults.</summary>
public static partial class TextCleanup
{
    [GeneratedRegex(@"\s*[\r\n]+\s*")]
    private static partial Regex LineBreakRun();

    public static string SingleLine(string text) => LineBreakRun().Replace(text, " ").Trim();

    public readonly record struct TruncateResult(string Text, bool Truncated);

    /// <summary>
    /// At least <paramref name="n"/> graphemes of <paramref name="text"/> on one line, whitespace runs as one
    /// space, and whether a visible grapheme was left out. It adds no marker: a caller that renders the result
    /// appends its own when <see cref="TruncateResult.Truncated"/> is true. For display only; the original is kept.
    /// </summary>
    public static TruncateResult Truncate(string text, int n)
    {
        if (n <= 0) return new TruncateResult("", false);
        var sb = new StringBuilder();
        int count = 0; // graphemes appended, including inserted spaces
        bool pendingSpace = false, budgetMet = false, truncated = false;

        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            string g = (string)e.Current;
            bool isWs = string.IsNullOrWhiteSpace(g);
            if (!budgetMet)
            {
                if (isWs)
                {
                    if (count > 0) pendingSpace = true;
                    continue;
                }
                if (pendingSpace) { sb.Append(' '); count++; pendingSpace = false; }
                sb.Append(g); count++;
                if (count >= n) budgetMet = true;
            }
            else if (!isWs)
            {
                truncated = true;
                break;
            }
        }
        return new TruncateResult(sb.ToString(), truncated);
    }
}
