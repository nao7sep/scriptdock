using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using ScriptDock.Models;

namespace ScriptDock.Services;

/// <summary>
/// Compiled ignore patterns for a scan, checked in order; the first that matches a path names why it was
/// left out. Every pattern ignores case, as the file systems this runs on do, and one ending in <c>/</c>
/// matches folders only.
/// <list type="bullet">
/// <item>A wildcard pattern reads names: <c>*</c> is any run of characters within a name, <c>?</c> one
/// character, <c>[abc]</c> one of the characters listed (<c>[a-z]</c> a range, <c>[!abc]</c> or <c>[^abc]</c>
/// any other) and <c>**</c> any number of folders, as in Git's ignore files, shells and ZipKit; every other
/// character is itself, and a <c>[</c> with no closing <c>]</c> too. A name holding brackets is matched
/// with a regular expression. With no <c>/</c> other
/// than a trailing one it matches a file or folder name at any depth; with one inside it, or a leading one, it
/// matches the path from the root directory being scanned, as in a .gitignore file.</item>
/// <item>A regular expression is matched anywhere in the full path, with <c>/</c> between folders and a
/// folder's path ending in <c>/</c>, so <c>/node_modules/</c> behaves the same on every platform. Its match
/// is bounded by a timeout, so a pathological pattern cannot hang a scan.</item>
/// </list>
/// Settings and the stored set accept only patterns that compile; one that still does not, or that times
/// out, is collected in <see cref="InvalidPatterns"/> and skipped rather than aborting the scan.
/// </summary>
public sealed class IgnoreRules
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private sealed class Rule(IgnorePattern pattern, Regex regex, bool byName)
    {
        public IgnorePattern Pattern { get; } = pattern;
        public Regex Regex { get; } = regex;
        // A wildcard with no slash inside reads the entry's name; any other pattern reads a path.
        public bool ByName { get; } = byName;
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

    public static IgnoreRules Compile(IEnumerable<IgnorePattern> patterns)
        => Compile(patterns, MatchTimeout);

    internal static IgnoreRules Compile(IEnumerable<IgnorePattern> patterns, TimeSpan matchTimeout)
    {
        var rules = new List<Rule>();
        var invalid = new List<string>();

        foreach (var pattern in patterns)
        {
            try
            {
                rules.Add(pattern.Kind == IgnorePatternKind.Regex
                    ? new Rule(pattern, new Regex(pattern.Pattern, Options, matchTimeout), byName: false)
                    : Wildcard(pattern, matchTimeout));
            }
            catch (ArgumentException)
            {
                invalid.Add(pattern.Pattern);
            }
        }

        return new IgnoreRules(rules, invalid);
    }

    /// <summary>Whether <paramref name="pattern"/> compiles: a regular expression that parses, or a wildcard
    /// that names something besides slashes.</summary>
    public static bool Compiles(IgnorePattern pattern)
    {
        try
        {
            _ = pattern.Kind == IgnorePatternKind.Regex ? new Regex(pattern.Pattern, Options) : Wildcard(pattern, MatchTimeout).Regex;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static Rule Wildcard(IgnorePattern pattern, TimeSpan matchTimeout)
    {
        var body = pattern.Pattern.TrimEnd('/');
        var anchored = body.Contains('/');
        body = body.TrimStart('/');
        if (body.Length == 0)
            throw new ArgumentException("A wildcard pattern names nothing.", nameof(pattern));
        return new Rule(pattern, new Regex("^" + WildcardRegex(body) + "$", Options, matchTimeout), byName: !anchored);
    }

    // * is any run within a name, ? one character within it, [...] one character from a class, ** any
    // number of whole folders.
    private static string WildcardRegex(string body)
    {
        var regex = new StringBuilder();
        for (var index = 0; index < body.Length; index++)
        {
            var c = body[index];
            if (c == '*' && index + 1 < body.Length && body[index + 1] == '*')
            {
                index++;
                // "**/" spans zero or more folders; a trailing or embedded "**" spans anything.
                if (index + 1 < body.Length && body[index + 1] == '/')
                {
                    index++;
                    regex.Append("(?:.*/)?");
                }
                else
                {
                    regex.Append(".*");
                }
            }
            else if (c == '*')
            {
                regex.Append("[^/]*");
            }
            else if (c == '?')
            {
                regex.Append("[^/]");
            }
            else if (c == '[' && CharacterClass(body, index) is { } @class)
            {
                regex.Append(@class.Regex);
                index = @class.End;
            }
            else
            {
                regex.Append(Regex.Escape(c.ToString()));
            }
        }
        return regex.ToString();
    }

    // A class from the [ at start to its closing ], or null when it has none and so is a literal [. A ] first
    // in the class, after any negation, is a member; a - between two members is a range; a class never
    // matches the / between folders.
    private static (string Regex, int End)? CharacterClass(string body, int start)
    {
        var index = start + 1;
        var negated = index < body.Length && body[index] is '!' or '^';
        if (negated)
            index++;
        var first = index;
        while (index < body.Length && (body[index] != ']' || index == first))
            index++;
        if (index >= body.Length)
            return null;

        var members = new StringBuilder();
        for (var at = first; at < index; at++)
        {
            var c = body[at];
            if (c == '/')
                continue;
            members.Append(c == '-' && at > first && at < index - 1 ? "-" : Regex.Escape(c.ToString()).Replace("]", "\\]").Replace("-", "\\-"));
        }
        // The lookahead keeps a range that spans / (such as +-0) from matching it.
        var regex = members.Length == 0 ? (negated ? "[^/]" : "(?!)") : $"(?!/)[{(negated ? "^" : "")}{members}]";
        return (regex, index);
    }

    /// <summary>
    /// The first pattern that matches <paramref name="path"/>, a file or folder under
    /// <paramref name="root"/>, or <c>null</c> when none does.
    /// </summary>
    public IgnorePattern? FirstMatch(string root, string path, bool isDirectory) =>
        FirstMatchCancellable(root, path, isDirectory, CancellationToken.None);

    internal IgnorePattern? FirstMatchCancellable(string root, string path, bool isDirectory, CancellationToken cancellationToken)
    {
        // Both separators read as /, on every platform, so a pattern means the same everywhere.
        var normalised = path.Replace('\\', '/').TrimEnd('/');
        var full = normalised + (isDirectory ? "/" : "");
        var name = normalised[(normalised.LastIndexOf('/') + 1)..];
        string? relative = null;

        foreach (var rule in _rules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rule.Disabled || (rule.Pattern.FoldersOnly && !isDirectory))
                continue;
            var subject = rule.Pattern.Kind == IgnorePatternKind.Regex ? full
                : rule.ByName ? name
                : relative ??= Relative(root, normalised);
            try
            {
                if (rule.Regex.IsMatch(subject))
                    return rule.Pattern;
            }
            catch (RegexMatchTimeoutException)
            {
                rule.Disabled = true;
                _invalidPatterns.Add(rule.Pattern.Pattern);
            }
        }

        return null;
    }

    // The path below the root, as an anchored wildcard reads it; matched ignoring case, as the scan does.
    private static string Relative(string root, string normalised)
    {
        var prefix = root.Replace('\\', '/').TrimEnd('/') + "/";
        return normalised.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? normalised[prefix.Length..] : normalised.TrimStart('/');
    }
}
