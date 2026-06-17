using System.Collections.Generic;

namespace ScriptDock.Models;

/// <summary>
/// Volatile session state, persisted to <c>~/.scriptdock/state.json</c>. Regenerable
/// UI state that should not churn the durable preferences in <see cref="AppConfig"/>.
/// </summary>
/// <remarks>
/// Phase 0 establishes the shape. Phase 1 adds window geometry and the recently-run
/// list; Phase 2 uses <see cref="KnownPaths"/> for the new/removed scan diff.
/// </remarks>
public sealed class AppState
{
    /// <summary>Whether hidden scripts are currently shown.</summary>
    public bool ShowHidden { get; set; }

    /// <summary>The set of script paths seen at the last acknowledged scan, against
    /// which the next scan computes its new/removed diff.</summary>
    public List<string> KnownPaths { get; set; } = [];
}
