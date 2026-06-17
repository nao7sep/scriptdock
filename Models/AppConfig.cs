using System.Collections.Generic;

namespace ScriptDock.Models;

/// <summary>
/// Durable user preferences, persisted to <c>~/.scriptdock/config.json</c>. These
/// survive across sessions and are the settings a user deliberately changes.
/// </summary>
/// <remarks>
/// Phase 0 establishes the shape with empty defaults. Phase 1 adds first-run seeding
/// (the platform-default extension, the built-in ignore patterns) and the behavior
/// that reads and edits these. Adding to this model later is forward-compatible — no
/// field declared here is expected to be removed.
/// </remarks>
public sealed class AppConfig
{
    /// <summary>Root directories scanned for scripts.</summary>
    public List<string> RootDirs { get; set; } = [];

    /// <summary>File extensions a script must have to be listed (e.g. <c>.command</c>).</summary>
    public List<string> Extensions { get; set; } = [];

    /// <summary>Regex patterns matched against full paths: a directory match prunes the
    /// subtree, a file match skips that file.</summary>
    public List<string> IgnorePatterns { get; set; } = [];

    /// <summary>Absolute paths the user has marked as favorites.</summary>
    public List<string> Favorites { get; set; } = [];

    /// <summary>Absolute paths the user has hidden from the default list.</summary>
    public List<string> Hidden { get; set; } = [];
}
