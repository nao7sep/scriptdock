using System.IO;

namespace ScriptDock.Storage;

/// <summary>
/// Canonical file names and resolved paths for the app's own data under
/// <c>~/.scriptdock/</c>. Keeps the store file names in one place so callers do not
/// repeat string literals. Paths resolve against <see cref="StorageRoot.Directory"/>,
/// so a test that calls <see cref="StorageRoot.Override"/> redirects these too.
/// </summary>
public static class AppPaths
{
    /// <summary>Durable preferences (see <see cref="Models.AppConfig"/>).</summary>
    public const string ConfigFileName = "config.json";

    /// <summary>Volatile session state (see <see cref="Models.AppState"/>).</summary>
    public const string StateFileName = "state.json";

    public static string ConfigFile => Path.Combine(StorageRoot.Directory, ConfigFileName);

    public static string StateFile => Path.Combine(StorageRoot.Directory, StateFileName);

    public static string LogsDirectory => StorageRoot.LogsDirectory;
}
