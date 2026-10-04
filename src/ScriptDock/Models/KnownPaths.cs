using System.Collections.Generic;
using System.Linq;
using ScriptDock.Storage;

namespace ScriptDock.Models;

/// <summary>
/// The script paths the last scan found, persisted to <c>~/.scriptdock/known-paths.json</c> so the next
/// scan can flag what is new and what is gone. Rebuildable (data-lifecycle-conventions): with no saved
/// list (no file, or an unreadable one rebuilt) the next scan flags nothing and saves what it found.
/// </summary>
public sealed class KnownPaths : IJsonNormalizable
{
    /// <summary>The saved list, or null when there is none. A file that loaded is always a list, even
    /// an empty one, so "no list" and "an empty list" stay apart.</summary>
    public List<string>? Paths { get; set; }

    public void NormalizeAfterLoad() => Paths = Paths?.OfType<string>().ToList() ?? [];
}
