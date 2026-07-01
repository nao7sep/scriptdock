using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using ScriptDock.Backup;
using ScriptDock.Storage;
using ScriptDock.Tests.Storage;
using Xunit;

namespace ScriptDock.Tests.Backup;

/// <summary>
/// End-to-end backup runs over a throwaway <c>SCRIPTDOCK_HOME</c>: a first run captures config.json at its
/// mirror path; an unchanged run writes nothing; an edit captures only what changed; a corrupt index resets
/// to a full backup; state.json and the excluded litter are never captured; and a case-insensitive entry
/// collision is skipped without failing the run.
/// </summary>
[Collection(StorageRootEnvironment.CollectionName)]
public sealed class BackupEngineTests : IDisposable
{
    private static readonly DateTimeOffset Run1 = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Run2 = new(2026, 7, 1, 1, 0, 0, TimeSpan.Zero);

    private readonly string? _previousHome;
    private readonly string _home;

    public BackupEngineTests()
    {
        _previousHome = Environment.GetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable);
        _home = Path.Combine(Path.GetTempPath(), "scriptdock-backup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, _previousHome);
        try { Directory.Delete(_home, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void First_Run_Captures_Config_At_Its_Mirror_Path()
    {
        WriteHome("config.json", "{\"a\":1}");

        var report = new BackupEngine().Run(Run1);

        Assert.Null(report.Fatal);
        Assert.False(report.NothingChanged);
        Assert.Equal(1, report.FilesArchived);
        Assert.Equal("backup-20260701-000000-utc.zip", report.ArchiveFileName);
        Assert.Equal(new[] { "config.json" }, ArchiveEntries("backup-20260701-000000-utc.zip"));

        // The index recorded exactly the one file, at the run's stamp, with camelCase keys.
        var index = LoadIndex();
        Assert.Single(index.Entries);
        Assert.Equal("20260701-000000-utc", index.Entries[0].ArchivedAt);
        Assert.Equal("config.json", index.Entries[0].ArchivePath);
    }

    [Fact]
    public void Second_Run_With_No_Changes_Writes_Nothing()
    {
        WriteHome("config.json", "{\"a\":1}");

        new BackupEngine().Run(Run1);
        var report = new BackupEngine().Run(Run2);

        Assert.True(report.NothingChanged);
        Assert.Null(report.ArchiveFileName);
        Assert.False(File.Exists(Path.Combine(AppPaths.BackupsDirectory, "backup-20260701-010000-utc.zip")));
    }

    [Fact]
    public void An_Edit_Captures_Only_The_Changed_File()
    {
        WriteHome("config.json", "{\"a\":1}");
        WriteHome("notes.txt", "hello");
        new BackupEngine().Run(Run1);

        WriteHome("config.json", "{\"a\":1,\"b\":2}"); // larger, so the change is caught regardless of mtime

        var report = new BackupEngine().Run(Run2);

        Assert.False(report.NothingChanged);
        Assert.Equal(1, report.FilesArchived);
        Assert.Equal(new[] { "config.json" }, ArchiveEntries("backup-20260701-010000-utc.zip"));
    }

    [Fact]
    public void A_Corrupt_Index_Is_Reset_And_Everything_Is_Recaptured()
    {
        WriteHome("config.json", "{\"a\":1}");
        new BackupEngine().Run(Run1);

        File.WriteAllText(AppPaths.BackupIndexFile, "{ this is not valid json");

        var report = new BackupEngine().Run(Run2);

        Assert.True(report.IndexWasReset);
        Assert.Equal(1, report.FilesArchived);
    }

    [Fact]
    public void State_Logs_Backups_And_Litter_Are_Never_Captured()
    {
        WriteHome("config.json", "{\"a\":1}");
        WriteHome("state.json", "{\"showHidden\":true}");
        WriteHome(".DS_Store", "junk");
        WriteHome("config.json.bak", "old");            // the retired sidecar
        WriteHome("config.json.abc.tmp", "partial");    // an atomic-write leftover
        WriteHome(Path.Combine("logs", "session.log"), "log line");

        var report = new BackupEngine().Run(Run1);

        Assert.Equal(1, report.FilesArchived);
        Assert.Equal(new[] { "config.json" }, ArchiveEntries("backup-20260701-000000-utc.zip"));
    }

    // The case-insensitive entry-collision behaviour is covered by BackupRootCollectorTests.DeduplicateFolds_*,
    // which exercise the pure dedup on any filesystem — a real-file test here cannot stage two case-differing
    // names on a case-insensitive host (macOS/Windows), where they are the same file.

    // --- helpers ---

    private void WriteHome(string relativePath, string content)
    {
        var full = Path.Combine(_home, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static BackupIndex LoadIndex()
    {
        var json = File.ReadAllText(AppPaths.BackupIndexFile);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        return JsonSerializer.Deserialize<BackupIndex>(json, options) ?? new BackupIndex();
    }

    private static string[] ArchiveEntries(string archiveName)
    {
        using var zip = ZipFile.OpenRead(Path.Combine(AppPaths.BackupsDirectory, archiveName));
        return zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }
}
