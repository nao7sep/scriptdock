using System;
using System.Collections.Generic;

namespace ScriptDock.Models;

/// <summary>A path a scan ignored, paired with the pattern responsible.</summary>
public sealed record IgnoredEntry(string Path, string Pattern);

/// <summary>
/// The outcome of one scan: what was found, what was pruned or skipped (and why), what
/// could not be read, and which patterns failed to compile. Drives both the one-line scan
/// log and the human-readable report a user reads to tune their ignore patterns.
/// </summary>
public sealed class ScanReport
{
    public required DateTimeOffset CompletedAt { get; init; }
    public required IReadOnlyList<string> Roots { get; init; }
    public required IReadOnlyList<string> Found { get; init; }
    public required IReadOnlyList<IgnoredEntry> PrunedDirectories { get; init; }
    public required IReadOnlyList<IgnoredEntry> SkippedFiles { get; init; }
    public required IReadOnlyList<string> Inaccessible { get; init; }
    public required IReadOnlyList<string> InvalidPatterns { get; init; }
}
