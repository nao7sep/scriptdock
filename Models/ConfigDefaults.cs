using System;
using System.Collections.Generic;
using System.IO;

namespace ScriptDock.Models;

/// <summary>
/// First-run defaults for <see cref="AppConfig"/>, applied only when no config file
/// exists yet (see <c>ConfigBootstrap</c>). Never applied to a merely-empty config — a
/// user who clears every extension or pattern has made a deliberate choice that must
/// survive a restart.
/// </summary>
public static class ConfigDefaults
{
    /// <summary>The launcher extension for the current platform: <c>.command</c> on macOS
    /// (and other Unix), <c>.ps1</c> on Windows.</summary>
    public static string DefaultExtension => OperatingSystem.IsWindows() ? ".ps1" : ".command";

    /// <summary>Minimal built-in ignore patterns — regex matched against full paths — that
    /// keep a scan out of vendored and build-output trees. Editable by the user.</summary>
    public static IReadOnlyList<string> BuiltInIgnorePatterns { get; } =
    [
        "/node_modules/",
        "/\\.git/",
        "/\\.svn/",
        "/\\.hg/",
        "/\\.venv/",
        "/venv/",
        "/__pycache__/",
        "/bin/",
        "/obj/",
        "/target/",
        "/dist/",
        "/build/",
        "/out/",
        "/publish/",
        "/\\.next/",
        "/Pods/",
    ];

    /// <summary>A config seeded for first run: the user's <c>~/code</c> as the initial
    /// root, the platform-default extension, and the built-in ignore patterns.</summary>
    public static AppConfig CreateSeededConfig() => new()
    {
        RootDirs = [DefaultRootDirectory()],
        Extensions = [DefaultExtension],
        IgnorePatterns = [.. BuiltInIgnorePatterns],
    };

    private static string DefaultRootDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "code");
}
