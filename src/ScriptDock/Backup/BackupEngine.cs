using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using ScriptDock.Storage;

namespace ScriptDock.Backup;

/// <summary>
/// Runs one backup pass and returns a <see cref="BackupReport"/>. It never throws for an expected problem
/// (a fatal error is captured in the report) and never logs — the caller logs the report. See the
/// data-backup conventions: change is size + mtime, the archive mirrors <c>~/.scriptdock/</c>, and the
/// archive is written and renamed into place <em>before</em> the index so a crash never records a phantom
/// backup.
/// </summary>
public sealed class BackupEngine
{
    private readonly string _root;
    private readonly string _backupsDirectory;
    private readonly string _indexFile;

    /// <summary>Builds an engine against the resolved storage root (<see cref="AppPaths.BackupsDirectory"/>).</summary>
    public BackupEngine()
        : this(StorageRoot.Directory, AppPaths.BackupsDirectory, AppPaths.BackupIndexFile)
    {
    }

    /// <summary>Builds an engine against explicit paths, so a test can point it at a throwaway home.</summary>
    public BackupEngine(string root, string backupsDirectory, string indexFile)
    {
        _root = root;
        _backupsDirectory = backupsDirectory;
        _indexFile = indexFile;
    }

    /// <summary>Captures everything changed since the last run. <paramref name="now"/> is injected so the
    /// archive stamp is deterministic under test.</summary>
    public BackupReport Run(DateTimeOffset now)
    {
        try
        {
            return RunCore(now);
        }
        catch (Exception ex)
        {
            return new BackupReport { Fatal = ex };
        }
    }

    private BackupReport RunCore(DateTimeOffset now)
    {
        var (index, indexReset) = LoadIndex();

        var collected = new BackupRootCollector(_root).Collect();
        var skips = new List<BackupSkip>(collected.Skips);

        var changed = BackupPlan.SelectChanged(collected.Candidates, index);
        if (changed.Count == 0)
        {
            return new BackupReport { NothingChanged = true, Skips = skips, IndexWasReset = indexReset };
        }

        var (archivedAt, archived) = WriteArchive(now, changed, skips);
        if (archived.Count == 0)
        {
            // Every changed file failed to read at archive time; nothing was written, so nothing is recorded.
            return new BackupReport { NothingChanged = true, Skips = skips, IndexWasReset = indexReset };
        }

        foreach (var item in archived)
        {
            index.Entries.Add(new BackupIndexEntry
            {
                ArchivedAt = archivedAt,
                ArchivePath = item.ArchivePath,
                SizeBytes = item.SizeBytes,
                LastWriteUtc = BackupTime.ToIsoSeconds(item.LastWriteUtc),
            });
        }

        // Index second: the archive is already safely in place, so a crash here just re-captures next run.
        SaveIndex(index);

        return new BackupReport
        {
            ArchiveFileName = ArchiveFileName(archivedAt),
            FilesArchived = archived.Count,
            Skips = skips,
            IndexWasReset = indexReset,
        };
    }

    private (BackupIndex Index, bool Reset) LoadIndex()
    {
        if (!File.Exists(_indexFile))
        {
            // Missing index is a normal first run: everything is new.
            return (new BackupIndex(), false);
        }

        try
        {
            var json = File.ReadAllText(_indexFile);
            return (JsonSerializer.Deserialize<BackupIndex>(json, JsonOptions.Default) ?? new BackupIndex(), false);
        }
        catch
        {
            // A corrupt index is reset to empty and treated as missing: the run becomes a full backup, which
            // costs one redundant archive, never data.
            TryDelete(_indexFile);
            return (new BackupIndex(), true);
        }
    }

    private void SaveIndex(BackupIndex index)
    {
        EnsureBackupsDirectory();
        var json = JsonSerializer.Serialize(index, JsonOptions.Default);
        var tempPath = TempPathFor(_indexFile);

        try
        {
            File.WriteAllText(tempPath, json);
            if (File.Exists(_indexFile))
            {
                File.Replace(tempPath, _indexFile, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, _indexFile);
            }
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    /// <summary>Writes the changed files to a temp zip, then renames it into place under the winning
    /// <c>archivedAt</c> stamp — a no-clobber create (see the data-backup conventions). If
    /// <c>backup-&lt;archivedAt&gt;.zip</c> already exists (e.g. a second instance stamped the same
    /// millisecond), the underlying instant is advanced one millisecond at a time until its stamp names a
    /// free archive; that winning stamp is returned so the caller records it on every index entry. Returns
    /// the files actually archived (a file unreadable at archive time is skipped, not recorded).</summary>
    private (string ArchivedAt, List<BackupCandidate> Archived) WriteArchive(
        DateTimeOffset now, IReadOnlyList<BackupCandidate> changed, List<BackupSkip> skips)
    {
        EnsureBackupsDirectory();
        var archivedAt = BackupTime.FileStamp(now);
        var tempPath = TempPathFor(Path.Combine(_backupsDirectory, ArchiveFileName(archivedAt)));

        var archived = new List<BackupCandidate>();
        try
        {
            using (var zip = ZipFile.Open(tempPath, ZipArchiveMode.Create))
            {
                foreach (var item in changed)
                {
                    try
                    {
                        zip.CreateEntryFromFile(item.SourcePath, item.ArchivePath);
                        archived.Add(item);
                    }
                    catch (Exception ex)
                    {
                        skips.Add(new BackupSkip(item.ArchivePath, "unreadable at archive time: " + ex.Message));
                    }
                }
            }

            if (archived.Count == 0)
            {
                TryDelete(tempPath);
                return (archivedAt, archived);
            }

            var finalPath = Path.Combine(_backupsDirectory, ArchiveFileName(archivedAt));
            while (File.Exists(finalPath))
            {
                now = now.AddMilliseconds(1);
                archivedAt = BackupTime.FileStamp(now);
                finalPath = Path.Combine(_backupsDirectory, ArchiveFileName(archivedAt));
            }

            File.Move(tempPath, finalPath);
            return (archivedAt, archived);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>Creates the backups directory lazily with the platform's default mode. Secrets are excluded
    /// from backups fleet-wide, so no archive can contain a secret and the directory needs no permission
    /// hardening (per the data-backup conventions).</summary>
    private void EnsureBackupsDirectory()
    {
        Directory.CreateDirectory(_backupsDirectory);
    }

    private static string ArchiveFileName(string archivedAt) => "backup-" + archivedAt + ".zip";

    // <stem>-<discriminator>.tmp, in the same directory as the target — per the derived-filename
    // grammar, never a suffix dot-appended after the full file name. No nanoid utility exists in
    // this app yet, so the discriminator stays a GUID.
    private static string TempPathFor(string targetPath)
    {
        var directory = Path.GetDirectoryName(targetPath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(targetPath);
        return Path.Combine(directory, $"{stem}-{Guid.NewGuid():N}.tmp");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort: a leftover temp is harmless and under backups/, which the walk excludes.
        }
    }
}
