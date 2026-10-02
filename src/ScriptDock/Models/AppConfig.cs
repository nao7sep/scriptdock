using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ScriptDock.Models;

/// <summary>
/// Durable user preferences, persisted to <c>~/.scriptdock/config.json</c>. These
/// survive across sessions and are the settings a user deliberately changes.
/// </summary>
public sealed class AppConfig
{
    /// <summary>The bundled default UI (chrome) font, registered via <c>.WithInterFont()</c>.</summary>
    public const string DefaultUiFontFamily = "Inter";

    /// <summary>
    /// The bundled Inter as the font manager reaches it. A bare "Inter" does NOT resolve
    /// to the embedded collection `.WithInterFont()` registers — with no system Inter
    /// installed it silently falls back to the platform default (Helvetica on macOS),
    /// whose ascent barely clears its cap height, so every label sits visibly high.
    /// The display name stays "Inter"; this URI is what actually loads it.
    /// </summary>
    public const string BundledUiFontUri = "fonts:Inter#Inter";

    /// <summary>The UI (chrome) font family. Family only; nothing is stored until the user types one.
    /// Empty shows the bundled default (Inter) as the field's placeholder and resolves to it. Applied
    /// app-wide; the read-only output console keeps its own monospace font.</summary>
    public string UiFontFamily { get; set; } = "";

    /// <summary>
    /// The interface language: a BCP 47 tag from the set, or <c>system</c> to follow the computer's
    /// own languages at each launch. Read before the app is built (<c>LanguageBootstrap</c>) so the
    /// first frame is already in it, and applied again on each Save. A missing or unknown value means
    /// system, so a hand-edited file can never leave the app without a language.
    /// </summary>
    public string Language { get; set; } = I18n.Languages.System;

    /// <summary>The app theme. System follows the OS; applied app-wide before the main window exists
    /// and again on each Save.</summary>
    [JsonConverter(typeof(ThemePreferenceJsonConverter))]
    public ThemePreference Theme { get; set; } = ThemePreference.System;

    /// <summary>Root directories scanned for scripts.</summary>
    public List<string> RootDirs { get; set; } = [];

    /// <summary>File extensions a script must have to be listed (e.g. <c>.command</c>).</summary>
    public List<string> Extensions { get; set; } = [ConfigDefaults.DefaultExtension];

    /// <summary>Regex patterns matched against full paths: a directory match prunes the
    /// subtree, a file match skips that file.</summary>
    public List<string> IgnorePatterns { get; set; } = [.. ConfigDefaults.BuiltInIgnorePatterns];

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
