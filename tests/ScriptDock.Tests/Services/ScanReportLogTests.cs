using System;
using System.IO;
using ScriptDock;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.Storage;
using ScriptDock.Tests.Storage;
using Xunit;

namespace ScriptDock.Tests.Services;

/// <summary>
/// The scan-report log filename is a machine-paced artifact — the app assigns it at runtime as
/// part of its own operation, per the timestamp conventions — so it carries the
/// millisecond-precision <c>FileStampMillis</c> stamp rather than the whole-second form: two
/// rescans that land in the same UTC second must not collide and clobber one report with another.
/// </summary>
[Collection(StorageRootEnvironment.CollectionName)]
public sealed class ScanReportLogTests : IDisposable
{
    private readonly string _root;
    private readonly string? _previousHome;

    public ScanReportLogTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "scriptdock-scanreportlog-tests", NanoId.New());
        Directory.CreateDirectory(_root);

        _previousHome = Environment.GetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable);
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, _previousHome);
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private static ScanReport ReportAt(DateTimeOffset completedAt) => new()
    {
        CompletedAt = completedAt,
        Roots = [],
        Found = [],
        PrunedDirectories = [],
        SkippedFiles = [],
        Inaccessible = [],
        InvalidPatterns = [],
    };

    [Fact]
    public void Write_NamesTheFileWithAMillisecondUtcStamp()
    {
        var completedAt = new DateTimeOffset(2026, 6, 17, 0, 15, 41, 123, TimeSpan.Zero);

        var path = ScanReportLog.Write(ReportAt(completedAt));

        Assert.Equal("scan-20260617-001541-123-utc.log", Path.GetFileName(path));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Write_FileNameMatchesTheMillisecondStampGrammar()
    {
        var path = ScanReportLog.Write(ReportAt(DateTimeOffset.UtcNow));

        Assert.Matches(@"^scan-\d{8}-\d{6}-\d{3}-utc\.log$", Path.GetFileName(path));
    }

    [Fact]
    public void Write_TwoRunsInTheSameWholeSecond_ProduceDistinctFilesRatherThanClobbering()
    {
        // Same UTC second, different milliseconds: the whole-second FileStamp would have
        // collided and the second run's WriteAllText would have clobbered the first report.
        var second = new DateTimeOffset(2026, 6, 17, 0, 15, 41, 0, TimeSpan.Zero);

        var firstPath = ScanReportLog.Write(ReportAt(second));
        var secondPath = ScanReportLog.Write(ReportAt(second.AddMilliseconds(500)));

        Assert.NotEqual(firstPath, secondPath);
        Assert.True(File.Exists(firstPath));
        Assert.True(File.Exists(secondPath));
    }
}
