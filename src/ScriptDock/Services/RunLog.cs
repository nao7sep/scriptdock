using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ScriptDock.Storage;

namespace ScriptDock.Services;

/// <summary>
/// The per-run output file: a script's stdout+stderr, written by the child shell itself
/// (not piped through ScriptDock), so a ScriptDock crash leaves the child no broken pipe.
/// Provides the log path for a run and a tail reader for the console — opened shared-read so
/// it reads while the run is still writing, returning the last <c>maxBytes</c> with ANSI
/// escapes stripped.
/// </summary>
public static class RunLog
{
    private const int DefaultTailBytes = 256 * 1024;

    /// <summary>The default runs directory, under the app's logs.</summary>
    public static string DefaultDirectory => Path.Combine(StorageRoot.LogsDirectory, "runs");

    /// <summary>A stable, filesystem-safe log path for a run.</summary>
    public static string PathFor(string directory, int id, string scriptPath, DateTimeOffset startedAt)
    {
        var name = Sanitize(Path.GetFileNameWithoutExtension(scriptPath));
        return Path.Combine(directory, $"{TimestampConventions.FileStamp(startedAt)}-{name}-{id}.log");
    }

    /// <summary>
    /// Reads the tail of a run log — the last <paramref name="maxBytes"/> bytes — as lines
    /// with ANSI escapes removed. A leading partial line (from cutting mid-file) is dropped.
    /// Returns empty if the file does not exist yet or cannot be read.
    /// </summary>
    public static IReadOnlyList<string> ReadTail(string path, int maxBytes = DefaultTailBytes) =>
        TryReadTail(path, out var lines, maxBytes) ? lines : Array.Empty<string>();

    internal static bool TryReadTail(string path, out IReadOnlyList<string> lines, int maxBytes = DefaultTailBytes)
    {
        lines = Array.Empty<string>();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            lines = ReadTail(stream, maxBytes);
            return true;
        }
        catch (Exception)
        {
            // Polling must tolerate missing, unreadable and malformed paths without logging every tick.
            // Failure is distinct from an empty log so the caller can retry without a length change.
            return false;
        }
    }

    internal static IReadOnlyList<string> ReadTail(Stream stream, int maxBytes)
    {
        if (maxBytes <= 0)
            return Array.Empty<string>();
        var length = stream.Length;
        var count = (int)Math.Min(length, maxBytes);
        var trimmed = length > count;
        stream.Seek(length - count, SeekOrigin.Begin);
        var bytes = new byte[count];
        var read = 0;
        while (read < count)
        {
            var received = stream.Read(bytes, read, count - read);
            if (received == 0) break;
            read += received;
        }
        // Decode only the captured extent. An actively writing child cannot extend this read forever.
        using var snapshot = new MemoryStream(bytes, 0, read);
        using var reader = new StreamReader(snapshot, Encoding.UTF8);
        var lines = Lines(reader.ReadToEnd());
        if (trimmed && lines.Count > 0)
            lines.RemoveAt(0);
        if (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    /// <summary>
    /// Output as a terminal shows it: one entry per line, each line's carriage-return redraws collapsed to
    /// what stays visible and its ANSI escapes removed. The console and the Records window read output this way.
    /// </summary>
    public static List<string> Lines(string output) =>
        output
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(CollapseCarriageReturns)
            .Select(AnsiStripper.Strip)
            .ToList();

    // Emulate a terminal's carriage-return overwrite: within a line, only the content after the
    // last bare '\r' stays visible — so a progress bar that redraws its line in place collapses to
    // its latest frame instead of one console line per redraw.
    private static string CollapseCarriageReturns(string line)
    {
        var index = line.LastIndexOf('\r');
        return index >= 0 ? line[(index + 1)..] : line;
    }

    private static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-');
        return sb.Length == 0 ? "run" : sb.ToString();
    }
}
