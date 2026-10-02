using System;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.Tests.Fakes;
using Xunit;

namespace ScriptDock.Tests.Services;

public sealed class ScanReportLogTests
{
    [Fact]
    public void Write_KeepsTheWholeReportAsARecord()
    {
        var records = new FakeRecordStore();
        var report = new ScanReport
        {
            CompletedAt = new DateTimeOffset(2026, 6, 17, 0, 15, 41, 123, TimeSpan.Zero),
            Roots = ["/r"],
            Found = ["/r/a.command"],
            PrunedDirectories = [new IgnoredEntry("/r/node_modules", "node_modules")],
            SkippedFiles = [],
            Inaccessible = [],
            InvalidPatterns = [],
        };

        ScanReportLog.Write(records, report);

        Assert.Same(report, Assert.Single(records.ScanReports));
    }
}
