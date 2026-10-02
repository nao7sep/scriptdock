using System.Collections.Generic;
using System.Linq;
using ScriptDock.Storage;

namespace ScriptDock.Models;

/// <summary>
/// The script paths the last scan found, persisted to <c>~/.scriptdock/known-paths.json</c> so the next
/// scan can flag what is new and what is gone. Rebuildable (data-lifecycle-conventions): losing it marks
/// every script new once.
/// </summary>
public sealed class KnownPaths : IJsonNormalizable
{
    public List<string> Paths { get; set; } = [];

    public void NormalizeAfterLoad() => Paths = Paths?.OfType<string>().ToList() ?? [];
}
