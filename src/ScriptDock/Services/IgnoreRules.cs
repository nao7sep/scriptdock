using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ScriptDock.Services;

/// <summary>
/// Compiled ignore patterns for a scan. Each pattern is a regular expression matched
/// against the full path with forward-slash separators — so a slash-wrapped pattern like
/// <c>/node_modules/</c> behaves the same on every platform. Matching is case-insensitive
/// (mirroring the case-insensitive filesystems this runs on) and bounded by a timeout, so
/// a pathological user pattern cannot hang a scan. Patterns that do not compile are
/// collected in <see cref="InvalidPatterns"/> and skipped, never aborting the scan.
/// </summary>
public sealed class IgnoreRules
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    private readonly IReadOnlyList<(string Pattern, Regex Regex)> _rules;

    public IReadOnlyList<string> InvalidPatterns { get; }

    private IgnoreRules(IReadOnlyList<(string, Regex)> rules, IReadOnlyList<string> invalidPatterns)
    {
        _rules = rules;
        InvalidPatterns = invalidPatterns;
    }

    public static IgnoreRules Compile(IEnumerable<string> patterns)
    {
        var rules = new List<(string, Regex)>();
        var invalid = new List<string>();

        foreach (var pattern in patterns)
        {
            try
            {
                rules.Add((pattern, new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout)));
            }
            catch (ArgumentException)
            {
                invalid.Add(pattern);
            }
        }

        return new IgnoreRules(rules, invalid);
    }

    /// <summary>
    /// Returns the first pattern that matches <paramref name="path"/>, or <c>null</c> if
    /// none do. Backslashes are normalised to forward slashes before matching.
    /// </summary>
    public string? FirstMatch(string path)
    {
        var normalised = path.Replace('\\', '/');

        foreach (var (pattern, regex) in _rules)
        {
            try
            {
                if (regex.IsMatch(normalised))
                    return pattern;
            }
            catch (RegexMatchTimeoutException)
            {
                // A pattern that times out on this path is treated as non-matching; the
                // scan continues rather than failing wholesale.
            }
        }

        return null;
    }
}
