using System;
using System.IO;

namespace ScriptDock.Storage;

/// <summary>
/// Shared home-anchored path resolution, per the storage-path conventions: expand a leading
/// <c>~</c> and make a value absolute against the user's home directory — never the working
/// directory, so a relative entry can never later resolve against cwd. Used by the storage-root
/// override (<see cref="StorageRoot"/>) and the scan-root editor so both anchor paths identically.
/// </summary>
internal static class HomePath
{
    /// <summary>
    /// Expands a leading <c>~</c> / <c>~/</c> in <paramref name="value"/> and returns it as a full,
    /// absolute path rooted at <paramref name="home"/>. A relative value is combined with
    /// <paramref name="home"/>, never the current directory. The path need not exist.
    /// </summary>
    public static string AnchorToHome(string value, string home)
    {
        if (value == "~")
            value = home;
        else if (value.StartsWith("~/", StringComparison.Ordinal) ||
                 value.StartsWith("~" + Path.DirectorySeparatorChar))
            value = Path.Combine(home, value[2..]);

        return Path.IsPathRooted(value)
            ? Path.GetFullPath(value)
            : Path.GetFullPath(Path.Combine(home, value));
    }
}
