using System;
using System.Collections.Generic;
using System.IO;
using ScriptDock.Storage;

namespace ScriptDock.Backup;

/// <summary>
/// Discovers what to back up by walking the app's home root, <c>~/.scriptdock/</c>. ScriptDock manages
/// only files under this root — the scripts it runs are external user content referenced from
/// <c>config.json</c>, not the app's own state — so there is nothing external to gather. Produces the
/// stat'd candidates for <see cref="BackupPlan"/> and records a <see cref="BackupSkip"/> for anything
/// unreadable or for a case-insensitive entry collision. All I/O here is metadata only — directory walks
/// and <see cref="FileInfo"/>; file contents are read later, when a changed file is archived.
/// </summary>
public sealed class BackupRootCollector
{
    private readonly string _root;

    /// <param name="root">The home root to walk (<see cref="StorageRoot.Directory"/> in production; a
    /// throwaway directory under test).</param>
    public BackupRootCollector(string root) => _root = root;

    public CollectedRoots Collect()
    {
        var candidates = new List<BackupCandidate>();
        var skips = new List<BackupSkip>();

        if (Directory.Exists(_root))
        {
            WalkHome(_root, _root, candidates, skips);
        }

        var deduped = DeduplicateFolds(candidates);
        skips.AddRange(deduped.Skips);
        return new CollectedRoots(deduped.Candidates, skips);
    }

    /// <summary>Walks the root, pruning the excluded <c>logs/</c> and <c>backups/</c> subtrees rather than
    /// walking and discarding them (backups/ can grow large). Symlinks/junctions are skipped rather than
    /// followed: a link could loop, or resolve outside the root and pull unrelated disk into the archive
    /// (data-backup conventions). Only real directories and regular files are considered.</summary>
    private void WalkHome(string root, string directory, List<BackupCandidate> candidates, List<BackupSkip> skips)
    {
        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(directory);
        }
        catch (Exception ex)
        {
            skips.Add(new BackupSkip(directory, "could not enumerate: " + ex.Message));
            return;
        }

        foreach (var entry in entries)
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(entry);
            }
            catch (Exception ex)
            {
                skips.Add(new BackupSkip(entry, "could not stat: " + ex.Message));
                continue;
            }

            // Never follow a symlink/junction — silently skip it (it is not the app's own data, and
            // following it risks a walk loop or an escape outside the root).
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            var relative = BackupArchivePaths.Normalize(Path.GetRelativePath(root, entry));
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                // Prune an excluded subtree (logs/, backups/) instead of descending into it.
                if (!HomeRootExclusions.IsExcluded(relative + "/"))
                {
                    WalkHome(root, entry, candidates, skips);
                }
            }
            else if (!HomeRootExclusions.IsExcluded(relative))
            {
                AddCandidate(candidates, skips, entry, BackupArchivePaths.ForHomeFile(relative));
            }
        }
    }

    private static void AddCandidate(
        List<BackupCandidate> candidates, List<BackupSkip> skips, string sourcePath, string archivePath)
    {
        try
        {
            var info = new FileInfo(sourcePath);
            candidates.Add(new BackupCandidate(
                sourcePath, archivePath, info.Length, BackupTime.ToWholeSecondUtc(info.LastWriteTimeUtc)));
        }
        catch (Exception ex)
        {
            skips.Add(new BackupSkip(sourcePath, "could not stat: " + ex.Message));
        }
    }

    /// <summary>
    /// Enforces case-insensitive uniqueness of archive entry paths: on a case-sensitive filesystem two
    /// entries can differ only by case, but a zip opened on a case-insensitive one would collide. Keep the
    /// first, record a skip for each later fold-collision, so a run never produces a corrupt archive. Pure
    /// (no I/O), so the fold-collision decision is unit-testable on any filesystem, independent of whether
    /// the host FS could actually produce the colliding pair.
    /// </summary>
    public static CollectedRoots DeduplicateFolds(IReadOnlyList<BackupCandidate> candidates)
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<BackupCandidate>();
        var skips = new List<BackupSkip>();
        foreach (var candidate in candidates)
        {
            if (seen.TryGetValue(candidate.ArchivePath, out var winner))
            {
                skips.Add(new BackupSkip(
                    candidate.SourcePath,
                    "case-insensitive entry collision with '" + winner + "'; kept the first"));
                continue;
            }

            seen[candidate.ArchivePath] = candidate.ArchivePath;
            kept.Add(candidate);
        }

        return new CollectedRoots(kept, skips);
    }
}

/// <summary>The candidates and skips a collection pass produced.</summary>
public sealed record CollectedRoots(
    IReadOnlyList<BackupCandidate> Candidates,
    IReadOnlyList<BackupSkip> Skips);
