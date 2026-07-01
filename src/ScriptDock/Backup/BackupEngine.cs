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

        var archivedAt = BackupTime.FileStamp(now);
        var archived = WriteArchive(archivedAt, changed, skips);
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
        var tempPath = _indexFile + "." + Guid.NewGuid().ToString("N") + ".tmp";

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

    /// <summary>Writes the changed files to a temp zip and renames it into place, returning the files that
    /// were actually archived (a file unreadable at archive time is skipped, not recorded).</summary>
    private List<BackupCandidate> WriteArchive(
        string archivedAt, IReadOnlyList<BackupCandidate> changed, List<BackupSkip> skips)
    {
        EnsureBackupsDirectory();
        var finalPath = Path.Combine(_backupsDirectory, ArchiveFileName(archivedAt));
        var tempPath = finalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

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
                return archived;
            }

            File.Move(tempPath, finalPath, overwrite: true);
            return archived;
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>Creates the backups directory lazily and, on POSIX, restricts it to the owner (0700) so an
    /// archive of an owner-only file (per the api-key-storage conventions) is never landed world-readable.
    /// Skipped on Windows, exactly as the storage conventions treat POSIX permissions.</summary>
    private void EnsureBackupsDirectory()
    {
        var created = Directory.CreateDirectory(_backupsDirectory);
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                created.UnixFileMode =
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            }
            catch
            {
                // Best effort: an inability to tighten the mode is not worth failing the backup over.
            }
        }
    }

    private static string ArchiveFileName(string archivedAt) => "backup-" + archivedAt + ".zip";

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
