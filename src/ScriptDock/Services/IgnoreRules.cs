using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;

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

    private sealed class Rule(string pattern, Regex regex)
    {
        public string Pattern { get; } = pattern;
        public Regex Regex { get; } = regex;
        public bool Disabled { get; set; }
    }

    private readonly IReadOnlyList<Rule> _rules;
    private readonly List<string> _invalidPatterns;

    public IReadOnlyList<string> InvalidPatterns => _invalidPatterns;

    private IgnoreRules(IReadOnlyList<Rule> rules, List<string> invalidPatterns)
    {
        _rules = rules;
        _invalidPatterns = invalidPatterns;
    }

    public static IgnoreRules Compile(IEnumerable<string> patterns)
        => Compile(patterns, MatchTimeout);

    internal static IgnoreRules Compile(IEnumerable<string> patterns, TimeSpan matchTimeout)
    {
        var rules = new List<Rule>();
        var invalid = new List<string>();

        foreach (var pattern in patterns)
        {
            try
            {
                rules.Add(new Rule(pattern, new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeout)));
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
    public string? FirstMatch(string path) => FirstMatchCancellable(path, CancellationToken.None);

    internal string? FirstMatchCancellable(string path, CancellationToken cancellationToken)
    {
        var normalised = path.Replace('\\', '/');

        foreach (var rule in _rules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rule.Disabled)
                continue;
            try
            {
                if (rule.Regex.IsMatch(normalised))
                    return rule.Pattern;
            }
            catch (RegexMatchTimeoutException)
            {
                rule.Disabled = true;
                _invalidPatterns.Add(rule.Pattern);
            }
        }

        return null;
    }
}
