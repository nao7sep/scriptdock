using ScriptDock.Models;
using ScriptDock.Storage;

namespace ScriptDock.Services;

/// <summary>
/// Keeps a <see cref="ScanReport"/> two ways: the whole report as a record (every pruned directory, skipped
/// file and the pattern responsible, which a user reads to tune their patterns), and one concise <c>info</c>
/// line with its counts, per the logging-conventions.
/// </summary>
public static class ScanReportLog
{
    public static void Write(IRecordStore records, ScanReport report)
    {
        records.AddScanReport(report);

        Log.Info("scan", new
        {
            roots = report.Roots.Count,
            found = report.Found.Count,
            prunedDirs = report.PrunedDirectories.Count,
            skippedFiles = report.SkippedFiles.Count,
            inaccessible = report.Inaccessible.Count,
            invalidPatterns = report.InvalidPatterns.Count,
        });
    }
}
