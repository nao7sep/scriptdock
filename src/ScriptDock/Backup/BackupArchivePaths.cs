namespace ScriptDock.Backup;

/// <summary>
/// Pure mapping from a home-root file's on-disk relative path to its entry path within the archive.
/// ScriptDock manages only files under <c>~/.scriptdock/</c> (the scripts it runs are external user
/// content referenced from <c>config.json</c>, not the app's own state), so the archive is a faithful
/// image of the home root: every captured file keeps its path relative to the root, with forward
/// slashes (<c>config.json</c> → <c>config.json</c>). See the data-backup conventions.
/// </summary>
public static class BackupArchivePaths
{
    /// <summary>Normalizes a filesystem-relative path to a forward-slash archive path.</summary>
    public static string Normalize(string relativePath) =>
        relativePath.Replace('\\', '/').TrimStart('/');

    /// <summary>A file that lives under <c>~/.scriptdock/</c>: its relative path is the archive path.</summary>
    public static string ForHomeFile(string relativePath) => Normalize(relativePath);
}
