namespace ScriptDock.Storage;

/// <summary>
/// Canonical file names for the app's own data under <c>~/.scriptdock/</c>, kept in one place so callers
/// do not repeat string literals. The store composes the file names against
/// <see cref="StorageRoot.Directory"/> (see <see cref="JsonStore{T}"/>), so a test that sets
/// <c>SCRIPTDOCK_HOME</c> redirects them all. The write-through backup store resolves its own file
/// (<c>backups.sqlite3</c>) against the same root inside <see cref="BackupStore"/>.
/// </summary>
public static class AppPaths
{
    /// <summary>Durable preferences (see <see cref="Models.AppConfig"/>).</summary>
    public const string ConfigFileName = "config.json";

    /// <summary>Volatile session state (see <see cref="Models.AppState"/>).</summary>
    public const string StateFileName = "state.json";
}
