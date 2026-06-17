using System;

namespace ScriptDock.Models;

/// <summary>
/// One entry in the recently-run list: the script that was launched and when. The list
/// is held newest-first, sorted on <see cref="RanAt"/>.
/// </summary>
public sealed class RecentRun
{
    /// <summary>Absolute path of the launched script.</summary>
    public required string Path { get; set; }

    /// <summary>When it was last launched. Stored UTC, ISO-8601 with millisecond precision
    /// (see <c>UtcMillisDateTimeOffsetConverter</c>).</summary>
    public required DateTimeOffset RanAt { get; set; }
}
