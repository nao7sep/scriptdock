using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ScriptDock.Services;

/// <summary>Central filesystem identity used whenever paths act as app-level keys.</summary>
public static class PathIdentity
{
    public static StringComparer Comparer { get; } =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    public static string Key(string path) => Key(path, 0);

    private static string Key(string path, int depth)
    {
        if (depth > 32)
            return Path.GetFullPath(path);

        var full = Path.GetFullPath(path);
        // realpath asks the mounted filesystem, so a case-insensitive APFS volume returns the
        // existing spelling while a case-sensitive APFS/external volume keeps two differently
        // cased files distinct. An OS-wide macOS comparer cannot express both volumes correctly.
        if (!OperatingSystem.IsWindows() && TryRealPath(full) is { } physical)
            return Path.TrimEndingDirectorySeparator(physical);

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

    private static string? TryRealPath(string path)
    {
        IntPtr resolved = IntPtr.Zero;
        try
        {
            resolved = RealPath(path, IntPtr.Zero);
            return resolved == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(resolved);
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (resolved != IntPtr.Zero)
                Free(resolved);
        }
    }

    [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    private static extern IntPtr RealPath(string path, IntPtr resolvedPath);

    [DllImport("libc", EntryPoint = "free")]
    private static extern void Free(IntPtr pointer);
}
