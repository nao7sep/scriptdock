using System;
using System.Collections.Generic;
using System.Linq;
using ScriptDock.Backup;
using Xunit;

namespace ScriptDock.Tests.Backup;

/// <summary>
/// The pure fold-collision dedup, exercised directly on candidates so the behaviour is verified on any
/// filesystem — independent of whether the host FS could actually produce two names differing only in case.
/// </summary>
public sealed class BackupRootCollectorTests
{
    private static BackupCandidate Candidate(string sourcePath, string archivePath) =>
        new(sourcePath, archivePath, SizeBytes: 1, LastWriteUtc: DateTimeOffset.UnixEpoch);

    [Fact]
    public void DeduplicateFolds_KeepsBoth_WhenNoNamesFoldTogether()
    {
        var result = BackupRootCollector.DeduplicateFolds(new List<BackupCandidate>
        {
            Candidate("/home/config.json", "config.json"),
            Candidate("/home/notes.txt", "notes.txt"),
        });

        Assert.Equal(2, result.Candidates.Count);
        Assert.Empty(result.Skips);
    }

    [Fact]
    public void DeduplicateFolds_KeepsFirst_AndSkipsTheCaseOnlyCollision()
    {
        var result = BackupRootCollector.DeduplicateFolds(new List<BackupCandidate>
        {
            Candidate("/home/README.txt", "README.txt"),
            Candidate("/home/readme.txt", "readme.txt"),
        });

        // The first wins; the second is dropped with a recorded skip, so the archive can never carry two
        // entries that collide on a case-insensitive filesystem.
        Assert.Single(result.Candidates);
        Assert.Equal("README.txt", result.Candidates[0].ArchivePath);

        var skip = Assert.Single(result.Skips);
        Assert.Equal("/home/readme.txt", skip.Path);
        Assert.Contains("case-insensitive entry collision", skip.Reason);
    }
}
