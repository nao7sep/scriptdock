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
    /// Built-in ignore patterns, read when the set is absent, each with a stable id that names its note. A
    /// minimal starting point for macOS and Windows developers in the major languages, in three groups:
    /// - Development folders, for scan speed: trees that tools download or generate, that are typically
    ///   large, and whose names are distinctive enough that nobody keeps their own scripts in them. Generic
    ///   names such as <c>build</c>, <c>dist</c>, <c>out</c> or a plain <c>bin</c> are left out, because a
    ///   project can keep real scripts there; per-script exclusion is the Hide button's job. From version
    ///   control through each ecosystem, in the order users meet them.
    /// - macOS system folders, then Windows system folders, which hold deleted, indexed or system data,
    ///   never a user's scripts. The systems' own files, such as <c>.DS_Store</c>, are left out: no script
    ///   matches them.
    /// Every one is a folder pattern; only the .NET build output needs a regular expression.
    /// </summary>
    public static IReadOnlyList<IgnorePattern> BuiltInIgnorePatterns { get; } =
    [
        new(".git/", Id: "dev.git"),                                       // version control internals
        new("node_modules/", Id: "dev.node-modules"),                      // JavaScript and TypeScript: installed packages
        new(".venv/", Id: "dev.venv"),                                     // Python: virtual environment
        new("venv/", Id: "dev.venv-plain"),                                // Python: the other common name
        new("__pycache__/", Id: "dev.pycache"),                            // Python: compiled bytecode
        new("/bin/(Debug|Release)/", IgnorePatternKind.Regex, "dotnet.bin"), // C# and .NET: build output (a plain bin is scanned)
        new("obj/", Id: "dotnet.obj"),                                     // C# and .NET: intermediate build files
        new(".gradle/", Id: "dev.gradle"),                                 // Java and Kotlin (Gradle): build cache
        new("target/", Id: "dev.target"),                                  // Rust and Java (Maven): build output
        new("vendor/", Id: "dev.vendor"),                                  // PHP (Composer), Go and Ruby (Bundler)
        new(".build/", Id: "dev.swift-build"),                             // Swift Package Manager: build output
        new("Pods/", Id: "dev.pods"),                                      // Swift and Objective-C (CocoaPods)
        new(".Trash/", Id: "macos.trash"),                                 // macOS: a home folder's Trash
        new(".Trashes/", Id: "macos.trashes"),                             // macOS: a drive's Trash
        new("__MACOSX/", Id: "macos.macosx"),                              // macOS: left by unzipping a Mac ZIP
        new(".Spotlight-V100/", Id: "macos.spotlight"),                    // macOS: a drive's search index
        new(".fseventsd/", Id: "macos.fseventsd"),                         // macOS: a drive's change record
        new(".TemporaryItems/", Id: "macos.temporary-items"),              // macOS: a drive's temporary files
        new(".DocumentRevisions-V100/", Id: "macos.document-revisions"),   // macOS: saved document versions
        new(".MobileBackups/", Id: "macos.mobile-backups"),                // macOS: local Time Machine snapshots
        new(".AppleDouble/", Id: "macos.apple-double-folder"),             // macOS: Mac data on other file systems
        new("$RECYCLE.BIN/", Id: "windows.recycle-bin"),                   // Windows: a drive's Recycle Bin
        new("RECYCLER/", Id: "windows.recycler"),                          // Windows XP: a drive's Recycle Bin
        new("RECYCLED/", Id: "windows.recycled"),                          // Windows 98: a drive's Recycle Bin
        new("System Volume Information/", Id: "windows.system-volume-information"), // Windows: restore points and index
    ];

    /// <summary>Each built-in's note, by its id: the catalogue key the list shows beside it.</summary>
    public static IReadOnlyDictionary<string, string> IgnoreNoteKeys { get; } = new Dictionary<string, string>
    {
        ["dev.git"] = "ignoreNote.git",
        ["dev.node-modules"] = "ignoreNote.nodeModules",
        ["dev.venv"] = "ignoreNote.venv",
        ["dev.venv-plain"] = "ignoreNote.venv",
        ["dev.pycache"] = "ignoreNote.pycache",
        ["dotnet.bin"] = "ignoreNote.dotnetBin",
        ["dotnet.obj"] = "ignoreNote.dotnetObj",
        ["dev.gradle"] = "ignoreNote.gradle",
        ["dev.target"] = "ignoreNote.target",
        ["dev.vendor"] = "ignoreNote.vendor",
        ["dev.swift-build"] = "ignoreNote.swiftBuild",
        ["dev.pods"] = "ignoreNote.pods",
        ["macos.trash"] = "ignoreNote.trash",
        ["macos.trashes"] = "ignoreNote.trashes",
        ["macos.macosx"] = "ignoreNote.macosx",
        ["macos.spotlight"] = "ignoreNote.spotlight",
        ["macos.fseventsd"] = "ignoreNote.fseventsd",
        ["macos.temporary-items"] = "ignoreNote.temporaryItems",
        ["macos.document-revisions"] = "ignoreNote.documentRevisions",
        ["macos.mobile-backups"] = "ignoreNote.mobileBackups",
        ["macos.apple-double-folder"] = "ignoreNote.appleDouble",
        ["windows.recycle-bin"] = "ignoreNote.recycleBin",
        ["windows.recycler"] = "ignoreNote.oldRecycleBin",
        ["windows.recycled"] = "ignoreNote.oldRecycleBin",
        ["windows.system-volume-information"] = "ignoreNote.systemVolume",
    };

    /// <summary>The list v0.1.0 stored as its seed, regular expressions as every pattern then was. It was
    /// never the user's choice, so it reads as the built-in rather than as a copy that would stop following it.</summary>
    internal static IReadOnlyList<string> V010SeededIgnorePatterns { get; } =
        ["/node_modules/", "/\\.venv/", "/venv/", "/__pycache__/", "/bin/", "/obj/", "/target/", "/\\.git/"];
}
