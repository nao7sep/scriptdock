namespace ScriptDock.Storage;

/// <summary>
/// Each store's format version, one number per format (store-recovery-conventions). A JSON store
/// records it as <c>formatVersion</c> (see <see cref="JsonStore{T}"/>), a SQLite store as
/// <c>PRAGMA user_version</c> (see <see cref="SqliteFormatVersion"/>).
/// </summary>
public static class FormatVersions
{
    /// <summary><c>config.json</c>.</summary>
    public const int Config = 1;

    /// <summary><c>state.json</c>.</summary>
    public const int State = 1;

    /// <summary><c>known-paths.json</c>.</summary>
    public const int KnownPaths = 1;

    /// <summary><c>records.sqlite3</c>.</summary>
    public const int Records = 1;

    /// <summary><c>backups.sqlite3</c>.</summary>
    public const int Backups = 1;
}
