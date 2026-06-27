using System.Collections.Generic;

namespace ScriptDock.Models;

/// <summary>
/// Durable user preferences, persisted to <c>~/.scriptdock/config.json</c>. These
/// survive across sessions and are the settings a user deliberately changes.
/// </summary>
/// <remarks>
/// The shape, with empty defaults; <c>ConfigDefaults</c> supplies the first-run seed
/// (the platform-default extension and the built-in ignore patterns), applied by
/// <c>ConfigBootstrap</c> only when no config file exists yet. Adding to this model
/// later is forward-compatible — no field declared here is expected to be removed.
/// </remarks>
public sealed class AppConfig
{
    /// <summary>The bundled default UI (chrome) font, registered via <c>.WithInterFont()</c>.</summary>
    public const string DefaultUiFontFamily = "Inter";

    /// <summary>The UI (chrome) font family. Family only; an empty value falls back to the bundled
    /// default (Inter). Applied app-wide; the read-only output console keeps its own monospace font.</summary>
    public string UiFontFamily { get; set; } = DefaultUiFontFamily;

    /// <summary>Root directories scanned for scripts.</summary>
    public List<string> RootDirs { get; set; } = [];

    /// <summary>File extensions a script must have to be listed (e.g. <c>.command</c>).</summary>
    public List<string> Extensions { get; set; } = [];

    /// <summary>Regex patterns matched against full paths: a directory match prunes the
    /// subtree, a file match skips that file.</summary>
    public List<string> IgnorePatterns { get; set; } = [];

    /// <summary>Absolute paths the user has hidden from the default list.</summary>
    public List<string> Hidden { get; set; } = [];

    /// <summary>When true, quitting ScriptDock terminates every running script (and its process
    /// tree). Default false: running scripts are left alive so an accidental quit does not kill
    /// in-progress work — they are recaptured next launch when <see cref="RecaptureProcessesOnLaunch"/>
    /// is on.</summary>
    public bool KillProcessesOnClose { get; set; }

    /// <summary>When true (default), a relaunch re-attaches to scripts left running by a previous
    /// session, matched by PID and OS start-time; otherwise those are treated as no longer running.</summary>
    public bool RecaptureProcessesOnLaunch { get; set; } = true;
}
