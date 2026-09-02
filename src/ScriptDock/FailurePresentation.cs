using System;

namespace ScriptDock;

/// <summary>Maps diagnostic exceptions to deliberately authored ScriptDock presentation copy.</summary>
public static class FailurePresentation
{
    public static string StartupStorage() =>
        "ScriptDock cannot use its storage location, so it has not opened scripts or changed settings. " +
        "Repair the SCRIPTDOCK_HOME location or its permissions, then start ScriptDock again.";

    public static string StartupData() =>
        "A settings file could not be read, and ScriptDock could not set it aside either, so it was " +
        "left unchanged rather than risk overwriting it. Your scripts are not affected. Repair or " +
        "move the affected file under the ScriptDock data folder, then start ScriptDock again.";

    public static string RecoveredData() =>
        "A settings file was unreadable, so ScriptDock preserved it and started with defaults in its " +
        "place. Your scripts are untouched. Check the session log for the preserved copy's location.";

    public static string RootPicker(Exception error) =>
        "The folder picker could not be opened. Your root directories are unchanged; try adding a directory again.";

    public static string ScriptStart(Exception error) => error is UnauthorizedAccessException
        ? "The script could not be started. Check that ScriptDock can run it, then try again."
        : "The script could not be started. Check the session log for details, then try again.";
}
