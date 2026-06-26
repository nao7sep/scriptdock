using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using ScriptDock.Storage;

namespace ScriptDock.Services;

internal enum LogRevealTargetKind
{
    File,
    Directory,
}

internal readonly record struct LogRevealTarget(string Path, LogRevealTargetKind Kind);

/// <summary>
/// Best-effort "show me the log" helper. Reveals this session's log file under
/// <see cref="StorageRoot.LogsDirectory"/> in the host platform's file manager (Finder on macOS,
/// Explorer on Windows). The logs directory also holds scan reports (<c>scan-*.log</c>) and the
/// per-run output logs, so it targets the known session-log path rather than the newest file;
/// when that path is unavailable (logging fell back to the console) it reveals the newest log, and
/// failing that opens the directory.
/// </summary>
public static class LogReveal
{
    public static void Reveal()
    {
        try
        {
            var target = SelectTarget(StorageRoot.LogsDirectory, Log.SessionLogPath, Log.Flush);
            if (target.Kind == LogRevealTargetKind.File)
                RevealInFileManager(target.Path);
            else
                OpenDirectoryInFileManager(target.Path);
        }
        catch (Exception ex)
        {
            Log.Error("reveal log: failed", ex);
        }
    }

    internal static LogRevealTarget SelectTarget(string logsDirectory, string? sessionLogPath, Action flush)
    {
        flush();
        Directory.CreateDirectory(logsDirectory);

        // Prefer this session's actual log file; it is the one the user means by "the log", and the
        // newest-file heuristic would otherwise surface a scan report written after it.
        if (sessionLogPath is not null && File.Exists(sessionLogPath))
            return new LogRevealTarget(sessionLogPath, LogRevealTargetKind.File);

        var current = TryFindMostRecentLog(logsDirectory);
        return current is not null
            ? new LogRevealTarget(current, LogRevealTargetKind.File)
            : new LogRevealTarget(logsDirectory, LogRevealTargetKind.Directory);
    }

    private static string? TryFindMostRecentLog(string dir)
    {
        try
        {
            return new DirectoryInfo(dir)
                .EnumerateFiles("*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()
                ?.FullName;
        }
        catch (Exception ex)
        {
            Log.Debug("reveal log: enumerate failed", ex, new { dir });
            return null;
        }
    }

    private static void RevealInFileManager(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // `open -R` selects and reveals the item in Finder.
            var psi = new ProcessStartInfo("open") { UseShellExecute = false };
            psi.ArgumentList.Add("-R");
            psi.ArgumentList.Add(path);
            Process.Start(psi)?.Dispose();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // explorer /select,PATH opens the parent folder and selects the file.
            // explorer.exe parses this as a single token; manual quoting is the
            // canonical form because ArgumentList's auto-quoting can confuse it.
            var psi = new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = false,
                Arguments = $"/select,\"{path}\"",
            };
            Process.Start(psi)?.Dispose();
        }
        else
        {
            OpenDirectoryInFileManager(Path.GetDirectoryName(path) ?? path);
        }
    }

    private static void OpenDirectoryInFileManager(string dir)
    {
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true })?.Dispose();
    }
}
