using System;
using System.Collections.Generic;
using System.IO;

namespace ScriptDock.Services;

/// <summary>The platform a script command targets, passed explicitly so both branches are testable on either OS.</summary>
public enum ScriptPlatform
{
    Unix,
    Windows,
}

/// <summary>
/// Resolves how to launch a script: the executable to run and its arguments. On Unix a
/// script runs through a login shell (<c>zsh -l -c</c>) so its PATH matches the user's
/// terminal — a GUI app launched from the Dock otherwise inherits a stripped PATH and
/// would not find node/dotnet/brew — referencing the script by its shell-quoted path so
/// the shebang and exec bit apply. On Windows, <c>.ps1</c> runs under <c>pwsh</c> and
/// <c>.bat</c>/<c>.cmd</c> under <c>cmd</c>.
/// </summary>
public readonly record struct ShellCommand(string FileName, IReadOnlyList<string> Arguments)
{
    public static ShellCommand For(string scriptPath) =>
        For(scriptPath, OperatingSystem.IsWindows() ? ScriptPlatform.Windows : ScriptPlatform.Unix);

    public static ShellCommand For(string scriptPath, ScriptPlatform platform)
    {
        if (platform == ScriptPlatform.Windows)
        {
            var ext = Path.GetExtension(scriptPath).ToLowerInvariant();
            return ext is ".bat" or ".cmd"
                ? new ShellCommand("cmd", ["/c", scriptPath])
                : new ShellCommand("pwsh", ["-NoLogo", "-File", scriptPath]);
        }

        return new ShellCommand("zsh", ["-l", "-c", ShellQuote(scriptPath)]);
    }

    private static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\\''") + "'";
}
