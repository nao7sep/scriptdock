using System;
using System.IO;

namespace ScriptDock.Services;

/// <summary>Central filesystem identity used whenever paths act as app-level keys.</summary>
public static class PathIdentity
{
    public static StringComparer Comparer { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    public static string Key(string path) => Key(path, 0);

    private static string Key(string path, int depth)
    {
        if (depth > 32)
            return Path.GetFullPath(path);

        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var current = root;
        var relative = full[root.Length..];

        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Length == 0)
                continue;

            var candidate = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(candidate)
                ? new DirectoryInfo(candidate)
                : new FileInfo(candidate);

            try
            {
                current = info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target
                    ? Key(target.FullName, depth + 1)
                    : candidate;
            }
            catch (IOException)
            {
                current = candidate;
            }
            catch (UnauthorizedAccessException)
            {
                current = candidate;
            }
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
    }

    public static bool Same(string left, string right) => Comparer.Equals(Key(left), Key(right));
}
