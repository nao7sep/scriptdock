using ScriptDock.Models;

namespace ScriptDock.Storage;

/// <summary>
/// The app's view-state and known-paths stores, each with its file, format version and recovery branch,
/// so the app and its tests build the same store. <c>config.json</c> is <see cref="ConfigStore"/>.
/// </summary>
public static class AppStores
{
    /// <summary>Not recorded: rebuildable, view state harmless to lose.</summary>
    public static JsonStore<AppState> State() =>
        new(AppPaths.StateFileName, "state", FormatVersions.State, recordBackups: false, rebuildable: true);

    /// <summary>Not recorded: rebuildable, the last scan's result.</summary>
    public static JsonStore<KnownPaths> KnownPaths() =>
        new(AppPaths.KnownPathsFileName, "known paths", FormatVersions.KnownPaths, recordBackups: false, rebuildable: true);
}
