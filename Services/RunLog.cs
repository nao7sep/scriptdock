using System;
using System.Collections.Generic;
using System.Globalization;
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
        var stamp = startedAt.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var name = Sanitize(Path.GetFileNameWithoutExtension(scriptPath));
        return Path.Combine(directory, $"{stamp}-utc-{name}-{id}.log");
    }

    /// <summary>
    /// Reads the tail of a run log — the last <paramref name="maxBytes"/> bytes — as lines
    /// with ANSI escapes removed. A leading partial line (from cutting mid-file) is dropped.
    /// Returns empty if the file does not exist yet or cannot be read.
    /// </summary>
    public static IReadOnlyList<string> ReadTail(string path, int maxBytes = DefaultTailBytes)
    {
        if (!File.Exists(path))
            return Array.Empty<string>();

        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            var trimmed = stream.Length > maxBytes;
            if (trimmed)
                stream.Seek(-maxBytes, SeekOrigin.End);

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = reader.ReadToEnd()
                .Replace("\r\n", "\n")
                .Split('\n')
                .Select(CollapseCarriageReturns)
                .Select(AnsiStripper.Strip)
                .ToList();

            // When we cut into the middle of the file, the first line is a fragment.
            if (trimmed && lines.Count > 0)
                lines.RemoveAt(0);

            // A trailing empty element from the final newline is noise.
            if (lines.Count > 0 && lines[^1].Length == 0)
                lines.RemoveAt(lines.Count - 1);

            return lines;
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
    }

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
