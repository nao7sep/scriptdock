using System.Globalization;
using System.IO;
using System.Text;
using ScriptDock.Models;
using ScriptDock.Storage;

namespace ScriptDock.Services;

/// <summary>
/// Persists a <see cref="ScanReport"/> two ways, per the logging-conventions: one concise
/// <c>info</c> line in the session log (counts only), and a full human-readable report
/// file under <c>~/.scriptdock/logs/</c> that enumerates every pruned directory, skipped
/// file, and the pattern responsible — the artifact a user reads to tune their patterns.
/// </summary>
public static class ScanReportLog
{
    /// <summary>Writes the report file and emits the summary log line; returns the report path.</summary>
    public static string Write(ScanReport report)
    {
        StorageRoot.EnsureExists();
        Directory.CreateDirectory(StorageRoot.LogsDirectory);

        var stamp = report.CompletedAt.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(StorageRoot.LogsDirectory, $"scan-{stamp}-utc.log");
        File.WriteAllText(path, Format(report));

        Log.Info("scan", new
        {
            roots = report.Roots.Count,
            found = report.Found.Count,
            prunedDirs = report.PrunedDirectories.Count,
            skippedFiles = report.SkippedFiles.Count,
            inaccessible = report.Inaccessible.Count,
            invalidPatterns = report.InvalidPatterns.Count,
            report = path,
        });

        return path;
    }

    private static string Format(ScanReport report)
    {
        var sb = new StringBuilder();
        sb.Append("ScriptDock scan — ")
          .Append(report.CompletedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
          .AppendLine(" UTC")
          .AppendLine();

        PathSection(sb, "Roots", report.Roots);

        sb.Append("Found: ").Append(report.Found.Count).AppendLine(" script(s)");
        foreach (var p in report.Found)
            sb.Append("  ").AppendLine(p);
        sb.AppendLine();

        EntrySection(sb, "Pruned directories", report.PrunedDirectories);
        EntrySection(sb, "Skipped files", report.SkippedFiles);
        PathSection(sb, "Inaccessible", report.Inaccessible);
        PathSection(sb, "Invalid patterns (ignored)", report.InvalidPatterns);

        return sb.ToString();
    }

    private static void PathSection(StringBuilder sb, string title, System.Collections.Generic.IReadOnlyList<string> values)
    {
        sb.Append(title).Append(": ").Append(values.Count).AppendLine();
        foreach (var v in values)
            sb.Append("  ").AppendLine(v);
        sb.AppendLine();
    }

    private static void EntrySection(StringBuilder sb, string title, System.Collections.Generic.IReadOnlyList<IgnoredEntry> entries)
    {
        sb.Append(title).Append(": ").Append(entries.Count).AppendLine();
        foreach (var e in entries)
            sb.Append("  ").Append(e.Path).Append("  <- ").AppendLine(e.Pattern);
        sb.AppendLine();
    }
}
