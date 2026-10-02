using System;
using System.Diagnostics;
using System.IO;
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
/// Best-effort "show me the log" helper. Reveals the records database, which holds the log, in the host
/// platform's file manager (Finder on macOS, Explorer on Windows); before the database exists it opens the
/// storage root, where the fallback files under <c>logs/</c> are.
/// </summary>
public static class LogReveal
{
    public static bool Reveal() => Reveal(Process.Start);

    internal static bool Reveal(Func<ProcessStartInfo, Process?> start)
    {
        try
        {
            var target = SelectTarget(Path.Combine(StorageRoot.Directory, RecordStore.FileName), StorageRoot.Directory, Log.Flush);
            var opened = OpenTarget(target, start);
            if (!opened)
                Log.Error("reveal log: no process returned", new { target = target.Path, kind = target.Kind.ToString() });
            return opened;
        }
        catch (Exception ex)
        {
            Log.Error("reveal log: failed", ex);
            return false;
        }
    }

    internal static bool OpenTarget(LogRevealTarget target, Func<ProcessStartInfo, Process?> start) =>
        target.Kind == LogRevealTargetKind.File
            ? RevealInFileManager(target.Path, start)
            : OpenDirectoryInFileManager(target.Path, start);

    internal static LogRevealTarget SelectTarget(string recordsFile, string directory, Action flush)
    {
        flush();
        return File.Exists(recordsFile)
            ? new LogRevealTarget(recordsFile, LogRevealTargetKind.File)
            : new LogRevealTarget(directory, LogRevealTargetKind.Directory);
    }

    private static bool RevealInFileManager(string path, Func<ProcessStartInfo, Process?> start)
    {
        Process? process;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // `open -R` selects and reveals the item in Finder.
            var psi = new ProcessStartInfo("open") { UseShellExecute = false };
            psi.ArgumentList.Add("-R");
            psi.ArgumentList.Add(path);
            process = start(psi);
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
            process = start(psi);
        }
        else
        {
            return OpenDirectoryInFileManager(Path.GetDirectoryName(path) ?? path, start);
        }

        process?.Dispose();
        return process is not null;
    }

    private static bool OpenDirectoryInFileManager(string dir, Func<ProcessStartInfo, Process?> start)
    {
        using var process = start(new ProcessStartInfo(dir) { UseShellExecute = true });
        return process is not null;
    }
}
