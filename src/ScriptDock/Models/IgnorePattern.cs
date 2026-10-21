using System.Text.Json.Serialization;

namespace ScriptDock.Models;

/// <summary>How an ignore pattern reads its text.</summary>
public enum IgnorePatternKind
{
    /// <summary>A wildcard pattern over names: <c>*</c>, <c>?</c> and <c>**</c> (<see cref="Services.IgnoreRules"/>).</summary>
    Wildcard,

    /// <summary>A regular expression over the full path, with <c>/</c> between folders.</summary>
    Regex,
}

/// <summary>
/// One ignore pattern: its text, how that text reads, and for a built-in its stable id, which names the
/// note the list shows beside it. A pattern ending in <c>/</c> matches folders only. Matching ignores case.
/// </summary>
public sealed record IgnorePattern(string Pattern, IgnorePatternKind Kind = IgnorePatternKind.Wildcard, string? Id = null)
{
    /// <summary>The built-in's id, kept so its note follows it; absent for a pattern of the user's own.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; init; } = Id;

    /// <summary>Whether the pattern matches folders only: it ends in <c>/</c>.</summary>
    [JsonIgnore]
    public bool FoldersOnly => Pattern.EndsWith('/');
}
