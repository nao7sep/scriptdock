using System;
using System.Collections.Generic;

namespace ScriptDock.Models;

/// <summary>Live built-in values used whenever a config set has no stored copy.</summary>
public static class ConfigDefaults
{
    /// <summary>The launcher extension for the current platform: <c>.command</c> on macOS
    /// (and other Unix), <c>.ps1</c> on Windows.</summary>
    public static string DefaultExtension => OperatingSystem.IsWindows() ? ".ps1" : ".command";

    /// <summary>
    /// Built-in ignore patterns, regexes matched against the full path, read when the set is absent: a
    /// minimal starting point for macOS and Windows developers in the major languages. Their one purpose is
    /// scan speed, so each skips a tree that tools download or generate, that is typically large, and whose
    /// name is distinctive enough that nobody keeps their own scripts in it. Generic names such as
    /// <c>build</c>, <c>dist</c>, <c>out</c> or a plain <c>bin</c> are left out, because a project can keep
    /// real scripts there; per-script exclusion is the Hide button's job. Grouped from version control through
    /// each ecosystem, in the order users meet them. Editable by the user.
    /// </summary>
    public static IReadOnlyList<string> BuiltInIgnorePatterns { get; } =
    [
        "/\\.git/",                 // version control internals, in every repository
        "/node_modules/",           // JavaScript and TypeScript: installed packages
        "/\\.venv/",                // Python: virtual environment
        "/venv/",                   // Python: virtual environment, the other common name
        "/__pycache__/",            // Python: compiled bytecode
        "/bin/(Debug|Release)/",    // C# and .NET: build output (a plain bin folder is still scanned)
        "/obj/",                    // C# and .NET: intermediate build files
        "/\\.gradle/",              // Java and Kotlin (Gradle): build cache
        "/target/",                 // Rust and Java (Maven): build output
        "/vendor/",                 // PHP (Composer), Go and Ruby (Bundler): vendored packages
        "/\\.build/",               // Swift Package Manager: build output and checkouts
        "/Pods/",                   // Swift and Objective-C (CocoaPods): installed pods
    ];

    /// <summary>The list v0.1.0 stored as its seed. It was never the user's choice, so it reads as the
    /// built-in rather than as a copy that would stop following it.</summary>
    internal static IReadOnlyList<string> V010SeededIgnorePatterns { get; } =
        ["/node_modules/", "/\\.venv/", "/venv/", "/__pycache__/", "/bin/", "/obj/", "/target/", "/\\.git/"];
}
