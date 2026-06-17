using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using ScriptDock.Models;

namespace ScriptDock.Services;

/// <summary>
/// Walks the configured root directories and classifies every entry: a directory whose
/// path matches an ignore pattern is pruned (never descended); a file whose extension is
/// configured is either found or, if its path matches an ignore pattern, skipped. Symbolic
/// links are not followed (so a link cycle cannot loop the walk), and a directory that
/// cannot be read is recorded as inaccessible rather than aborting the scan.
/// </summary>
public sealed class ScriptScanner
{
    public ScanReport Scan(
        IReadOnlyList<string> rootDirs,
        IReadOnlyList<string> extensions,
        IReadOnlyList<string> ignorePatterns,
        CancellationToken cancellationToken = default)
    {
        var rules = IgnoreRules.Compile(ignorePatterns);
        var extensionSet = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);

        var found = new List<string>();
        var prunedDirectories = new List<IgnoredEntry>();
        var skippedFiles = new List<IgnoredEntry>();
        var inaccessible = new List<string>();

        var roots = rootDirs
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(Path.GetFullPath)
            .ToList();

        var stack = new Stack<string>();
        foreach (var root in roots)
        {
            if (Directory.Exists(root))
                stack.Push(root);
            else
                inaccessible.Add(root);
        }

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dir = stack.Pop();

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
                if (IsSymbolicLink(subDir))
                    continue;

                // Append a separator so a slash-wrapped pattern matches the directory
                // itself, not only files beneath it.
                var match = rules.FirstMatch(subDir + Path.DirectorySeparatorChar);
                if (match is not null)
                    prunedDirectories.Add(new IgnoredEntry(subDir, match));
                else
                    stack.Push(subDir);
            }

            foreach (var file in files)
            {
                if (!extensionSet.Contains(Path.GetExtension(file)))
                    continue;

                var match = rules.FirstMatch(file);
                if (match is not null)
                    skippedFiles.Add(new IgnoredEntry(file, match));
                else
                    found.Add(file);
            }
        }

        return new ScanReport
        {
            CompletedAt = DateTimeOffset.UtcNow,
            Roots = roots,
            Found = SortedPaths(found),
            PrunedDirectories = SortedEntries(prunedDirectories),
            SkippedFiles = SortedEntries(skippedFiles),
            Inaccessible = SortedPaths(inaccessible),
            InvalidPatterns = rules.InvalidPatterns,
        };
    }

    private static bool IsSymbolicLink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            // If attributes cannot be read, err toward not descending.
            return true;
        }
    }

    private static IReadOnlyList<string> SortedPaths(List<string> values)
    {
        values.Sort(StringComparer.Ordinal);
        return values;
    }

    private static IReadOnlyList<IgnoredEntry> SortedEntries(List<IgnoredEntry> entries)
    {
        entries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return entries;
    }
}
