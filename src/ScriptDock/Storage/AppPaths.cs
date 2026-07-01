using System.IO;

namespace ScriptDock.Storage;

/// <summary>
/// Canonical file names and derived paths for the app's own data under <c>~/.scriptdock/</c>, kept in
/// one place so callers do not repeat string literals. The store composes the file names against
/// <see cref="StorageRoot.Directory"/> (see <see cref="JsonStore{T}"/>); the backup paths are resolved
/// against the same root, so a test that sets <c>SCRIPTDOCK_HOME</c> redirects them all too.
/// </summary>
public static class AppPaths
{
    /// <summary>Durable preferences (see <see cref="Models.AppConfig"/>).</summary>
    public const string ConfigFileName = "config.json";

    /// <summary>Volatile session state (see <see cref="Models.AppState"/>).</summary>
    public const string StateFileName = "state.json";

    /// <summary>The directory name the backup feature keeps its archives and index in.</summary>
    public const string BackupsDirectoryName = "backups";

    /// <summary>
    /// Where the startup data-backup feature keeps its archives and index. Not created eagerly: the
    /// backup engine makes it lazily on the first run that actually writes an archive, so a launch
    /// with nothing to back up leaves the root untouched.
    /// </summary>
    public static string BackupsDirectory => Path.Combine(StorageRoot.Directory, BackupsDirectoryName);

    /// <summary>The backup change ledger, <c>backups/index.json</c> (see the data-backup conventions).</summary>
    public static string BackupIndexFile => Path.Combine(BackupsDirectory, "index.json");
}
