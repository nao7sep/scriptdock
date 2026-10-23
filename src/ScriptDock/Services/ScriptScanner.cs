using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using ScriptDock.Models;

namespace ScriptDock.Services;

/// <summary>
/// Walks the configured root directories and classifies every entry: a directory an ignore
/// pattern matches is pruned (never descended); a file whose extension is configured is either
/// found or, if an ignore pattern matches it, skipped. Symbolic
/// links are not followed (so a link cycle cannot loop the walk), and a directory that
/// cannot be read is recorded as inaccessible rather than aborting the scan.
/// </summary>
public sealed class ScriptScanner
{
    private readonly Func<string, FileAttributes> _getAttributes;
    private readonly Func<string, string> _pathKey;

    public ScriptScanner() : this(File.GetAttributes, PathIdentity.Key) { }

    internal ScriptScanner(Func<string, FileAttributes> getAttributes, Func<string, string>? pathKey = null)
    {
        _getAttributes = getAttributes;
        _pathKey = pathKey ?? PathIdentity.Key;
    }

    public ScanReport Scan(
        IReadOnlyList<string> rootDirs,
        IReadOnlyList<string> extensions,
        IReadOnlyList<IgnorePattern> ignorePatterns,
        CancellationToken cancellationToken = default)
    {
        var rules = IgnoreRules.Compile(ignorePatterns);
        var identities = new Dictionary<string, string>(StringComparer.Ordinal);
        string Key(string path)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!identities.TryGetValue(path, out var key)) identities[path] = key = _pathKey(path);
            return key;
        }

        // Normalize each configured extension to the leading-dot form Path.GetExtension returns, and
        // drop blanks. The Settings dialog normalizes on entry, but config.json is an editable surface
        // (storage-path conventions) — a hand-edited "command" (no dot) or "" would otherwise match
        // nothing and the scan would silently come back empty.
        var extensionSet = new HashSet<string>(
            extensions
                .Select(e => e.Trim())
                .Where(e => e.Length > 0)
                .Select(e => e.StartsWith('.') ? e : "." + e),
            StringComparer.OrdinalIgnoreCase);

        var found = new List<string>();
        var prunedDirectories = new List<IgnoredEntry>();
        var skippedFiles = new List<IgnoredEntry>();
        var inaccessible = new List<string>();

        var roots = rootDirs
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(Path.GetFullPath)
            .GroupBy(Key, PathIdentity.Comparer)
            .Select(group => group.First())
            .ToList();

        // Each folder still to read, with the root it was found under, which an anchored pattern reads from.
        var stack = new Stack<(string Root, string Dir)>();
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(root))
                stack.Push((root, root));
            else
                inaccessible.Add(UnreadableRoot(root));
        }

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (root, dir) = stack.Pop();

            List<string> subDirs;
            List<string> files;
            try
            {
                subDirs = Directory.EnumerateDirectories(dir).ToList();
                files = Directory.EnumerateFiles(dir).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                inaccessible.Add(dir);
                continue;
            }

            foreach (var subDir in subDirs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if ((_getAttributes(subDir) & FileAttributes.ReparsePoint) != 0)
                        continue;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    // An unavailable attribute probe cannot establish that this is an excluded link.
                    // Mark the subtree unobserved so remembered scripts survive the incomplete scan.
                    inaccessible.Add(subDir);
                    continue;
                }

                var match = rules.FirstMatchCancellable(root, subDir, isDirectory: true, cancellationToken);
                if (match is not null)
                    prunedDirectories.Add(new IgnoredEntry(subDir, match.Pattern));
                else
                    stack.Push((root, subDir));
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!extensionSet.Contains(Path.GetExtension(file)))
                    continue;

                var match = rules.FirstMatchCancellable(root, file, isDirectory: false, cancellationToken);
                if (match is not null)
                    skippedFiles.Add(new IgnoredEntry(file, match.Pattern));
                else
                    found.Add(file);
            }
        }

        return new ScanReport
        {
            CompletedAt = DateTimeOffset.UtcNow,
            Roots = roots,
            Found = SortedPaths(found, cancellationToken, Key),
            PrunedDirectories = SortedEntries(prunedDirectories),
            SkippedFiles = SortedEntries(skippedFiles),
            Inaccessible = SortedPaths(inaccessible, cancellationToken, Key),
            PathKeys = identities,
            InvalidPatterns = rules.InvalidPatterns,
        };
    }

    private static string UnreadableRoot(string root)
    {
        // Exists suppresses the reason for failure. Probe the named root: an explicitly missing
        // sibling says nothing about a readable root, even if their parent cannot be listed.
        try
        {
            _ = File.GetAttributes(root);
        }
        catch (UnauthorizedAccessException) when (!OperatingSystem.IsWindows())
        {
            // POSIX stat needs ancestor traversal, not directory listing permission. Preserve the
            // nearest observable ancestor when traversal is denied: nested case/link spellings
            // cannot then be resolved safely. Windows attribute denial need not imply this scope.
            var parent = Directory.GetParent(root);
            while (parent is not null && !Directory.Exists(parent.FullName)) parent = parent.Parent;
            return parent?.FullName ?? root;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        return root;
    }

    private static IReadOnlyList<string> SortedPaths(List<string> values, CancellationToken cancellationToken, Func<string, string> key)
    {
        // Distinct: overlapping or nested roots (e.g. /code and /code/proj) enumerate the same file
        // under more than one walk, so dedup by physical identity — otherwise a script shows as a duplicate
        // tile and the status-bar total/hidden counts (derived from Found.Count) are inflated.
        var distinct = values.DistinctBy(path =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return key(path);
        }, PathIdentity.Comparer).ToList();
        cancellationToken.ThrowIfCancellationRequested();
        distinct.Sort(StringComparer.Ordinal);
        return distinct;
    }

    private static IReadOnlyList<IgnoredEntry> SortedEntries(List<IgnoredEntry> entries)
    {
        entries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return entries;
    }
}
