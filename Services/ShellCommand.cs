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
/// Resolves how to launch a script with its output redirected to a run-log file. On Unix a
/// script runs through a login shell (<c>zsh -l -c</c>) so its PATH matches the user's
/// terminal; the shell sends stdout+stderr to the log file (so ScriptDock holds no pipe to the
/// child's output — a crash can't break the child's writes). The child's stdin is left inherited
/// from ScriptDock, which redirects it to a pipe so the user can type into an interactive script;
/// a ScriptDock crash merely EOFs that stdin, which a script handles like a closed terminal.
/// On Windows, <c>.ps1</c> runs under <c>pwsh</c> (output to the file) and <c>.bat</c>/<c>.cmd</c>
/// under <c>cmd</c>.
/// </summary>
/// <remarks>
/// Because the child writes to a file rather than a TTY, a program that block-buffers its
/// stdout (C stdio without flushing) may have its output appear in chunks rather than line
/// by line. Shell builtins and most dev tooling (node, npm) write promptly; faithful line
/// buffering would need a PTY, which would re-introduce a pipe and defeat the crash-safety
/// this buys.
/// </remarks>
public readonly record struct ShellCommand(string FileName, IReadOnlyList<string> Arguments)
{
    public static ShellCommand ForRun(string scriptPath, string logPath) =>
        ForRun(scriptPath, logPath, OperatingSystem.IsWindows() ? ScriptPlatform.Windows : ScriptPlatform.Unix);

    public static ShellCommand ForRun(string scriptPath, string logPath, ScriptPlatform platform)
    {
        if (platform == ScriptPlatform.Windows)
        {
            var ext = Path.GetExtension(scriptPath).ToLowerInvariant();
            return ext is ".bat" or ".cmd"
                ? new ShellCommand("cmd", ["/c", $"{CmdQuote(scriptPath)} > {CmdQuote(logPath)} 2>&1"])
                : new ShellCommand("pwsh", ["-NoLogo", "-Command", $"& {PwshQuote(scriptPath)} *> {PwshQuote(logPath)}"]);
        }

        return new ShellCommand("zsh", ["-l", "-c", $"{ShellQuote(scriptPath)} > {ShellQuote(logPath)} 2>&1"]);
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static string PwshQuote(string value) => "'" + value.Replace("'", "''") + "'";

    private static string CmdQuote(string value) => "\"" + value + "\"";
}
