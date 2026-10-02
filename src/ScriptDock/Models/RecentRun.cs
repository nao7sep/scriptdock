using System;

namespace ScriptDock.Models;

/// <summary>
/// One entry in the Recent list: the script that was launched and when, as the run records give
/// it. The list is held newest-first, sorted on <see cref="RanAt"/>.
/// </summary>
public sealed class RecentRun
{
    /// <summary>Absolute path of the launched script.</summary>
    public required string Path { get; set; }

    /// <summary>When it was last launched, UTC.</summary>
    public required DateTimeOffset RanAt { get; set; }
}
