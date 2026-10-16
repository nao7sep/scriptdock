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
    public static List<string> Lines(string output)
    {
        var lines = new List<string>();
        var line = new StringBuilder();
        var cleaner = new OutputCleaner(text => line.Append(text), () =>
        {
            lines.Add(line.ToString());
            line.Clear();
        });
        cleaner.Write(output);
        cleaner.Complete();
        lines.Add(line.ToString());
        return lines;
    }

    /// <summary>
    /// Writes <paramref name="output"/> as <see cref="Lines"/> reads it, joined by line feeds, in one pass of
    /// pieces, so memory stays at a working buffer whatever the output's size (volume-handling-conventions).
    /// </summary>
    public static void Copy(Stream output, TextWriter destination)
    {
        using var reader = new StreamReader(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024, leaveOpen: true);
        var cleaner = new OutputCleaner(destination.Write, () => destination.Write('\n'));
        var buffer = new char[64 * 1024];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            cleaner.Write(buffer.AsSpan(0, read));
        cleaner.Complete();
    }

    /// <summary>
    /// Cleans output as a terminal shows it, in one pass over pieces of any size: a line feed (or CRLF) ends a
    /// line, a bare carriage return redraws the line from its start, and ANSI escapes are removed. A line is
    /// held until it ends, except that one growing past a working buffer with no line break is passed on in
    /// pieces, after which a later carriage return in it redraws only what is still held.
    /// </summary>
    internal sealed class OutputCleaner(Action<string> text, Action lineEnd)
    {
        // A working buffer, not a limit on a line: past it, the line is passed on in pieces.
        internal const int HeldChars = 1024 * 1024;

        // How far back an escape may start and still be held whole when a long line is passed on.
        private const int EscapeReach = 4096;

        private readonly StringBuilder _held = new();
        private bool _pendingReturn;

        public void Write(ReadOnlySpan<char> chars)
        {
            foreach (var c in chars)
            {
                if (_pendingReturn)
                {
                    _pendingReturn = false;
                    if (c == '\n')
                    {
                        EndLine();
                        continue;
                    }
                    _held.Clear(); // a bare carriage return: the line is redrawn from its start
                }

                if (c == '\r')
                    _pendingReturn = true;
                else if (c == '\n')
                    EndLine();
                else
                {
                    _held.Append(c);
                    if (_held.Length >= HeldChars)
                        PassOnPart();
                }
            }
        }

        /// <summary>The end of the line still arriving, as it shows now, without waiting for its line end.</summary>
        public string Held => AnsiStripper.Strip(_held.ToString());

        /// <summary>The output ended: what is held is the last line, which has no line end.</summary>
        public void Complete()
        {
            if (_pendingReturn)
            {
                _pendingReturn = false;
                _held.Clear();
            }
            text(AnsiStripper.Strip(_held.ToString()));
            _held.Clear();
        }

        private void EndLine()
        {
            text(AnsiStripper.Strip(_held.ToString()));
            _held.Clear();
            lineEnd();
        }

        // Pass on all but an escape that may still be arriving, so it is removed whole.
        private void PassOnPart()
        {
            var cut = _held.Length;
            for (var index = _held.Length - 1; index >= Math.Max(0, _held.Length - EscapeReach); index--)
            {
                if (_held[index] == '\x1b')
                {
                    cut = index;
                    break;
                }
            }
            text(AnsiStripper.Strip(_held.ToString(0, cut)));
            _held.Remove(0, cut);
        }
    }

    private static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-');
        return sb.Length == 0 ? "run" : sb.ToString();
    }
}
