using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ScriptDock.Models;

// Inaccessibility is absence of evidence, not evidence that an acknowledged script was deleted.
// Keep only unobserved subtrees; accessible deletions and discoveries still apply on this scan.
public static class ScanCatalog
{
    public static IReadOnlyList<string> Paths(ScanReport report, IEnumerable<string> previous, Func<string, string> key) =>
        Paths(report, previous, key, ignoreCase: OperatingSystem.IsWindows());

    // Windows paths compare without case; macOS keys are already the volume's own spelling.
    internal static IReadOnlyList<string> Paths(
        ScanReport report, IEnumerable<string> previous, Func<string, string> key, bool ignoreCase)
    {
        if (report.Inaccessible.Count == 0) return report.Found;
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefixes = report.Inaccessible.Select(path => key(path))
            .Select(path => Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar).ToList();
        return report.Found.Concat(previous.Select(path => (Path: path, Key: key(path)))
                .Where(path => prefixes.Any(prefix => path.Key.StartsWith(prefix, comparison))).Select(path => path.Path))
            .Distinct(ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToList();
    }
}
