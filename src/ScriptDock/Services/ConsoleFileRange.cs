using System;
using System.IO;
using System.Text;
using System.Threading;

namespace ScriptDock.Services;

/// <summary>A captured line extent in the authoritative log. Copy reads this extent, never later appends.</summary>
public sealed record ConsoleFileRange(string Path, long Start, long End)
{
    public string ReadText(CancellationToken cancellation = default)
    {
        using var file = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        file.Position = Start;
        var result = new StringBuilder();
        var cleaner = new RunLog.OutputCleaner(text => result.Append(text), () => result.Append('\n'));
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[64 * 1024];
        var chars = new char[bytes.Length + 1];
        var left = End - Start;
        while (left > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            var count = file.Read(bytes, 0, (int)Math.Min(bytes.Length, left));
            if (count == 0) throw new EndOfStreamException("The selected output extent is no longer available.");
            left -= count;
            var decoded = decoder.GetChars(bytes, 0, count, chars, 0, flush: false);
            cleaner.Write(chars.AsSpan(0, decoded));
        }
        cleaner.Complete();
        if (result.Length > 0 && result[^1] == '\n') result.Length--;
        return result.ToString();
    }
}
