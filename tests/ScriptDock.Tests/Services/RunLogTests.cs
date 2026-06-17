using System;
using System.IO;
using System.Text;
using ScriptDock.Services;
using Xunit;

namespace ScriptDock.Tests.Services;

public sealed class RunLogTests : IDisposable
{
    // ESC (0x1B) built from its code point, so the source carries no invisible control byte.
    private const char Esc = (char)27;

    private readonly string _dir;
    private readonly string _file;

    public RunLogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "scriptdock-runlog-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "run.log");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void ReadTail_MissingFile_ReturnsEmpty()
    {
        Assert.Empty(RunLog.ReadTail(Path.Combine(_dir, "nope.log")));
    }

    [Fact]
    public void ReadTail_StripsAnsi_AndDropsTrailingBlank()
    {
        File.WriteAllText(_file, $"{Esc}[32mok{Esc}[0m\nsecond\n");

        Assert.Equal(["ok", "second"], RunLog.ReadTail(_file));
    }

    [Fact]
    public void ReadTail_CapsToLastBytes_AndDropsPartialFirstLine()
    {
        File.WriteAllText(_file, "aaaa\nbbbb\ncccc\n");

        var tail = RunLog.ReadTail(_file, maxBytes: 10);

        Assert.DoesNotContain("aaaa", tail);
        Assert.Contains("cccc", tail);
    }

    [Fact]
    public void ReadTail_ReadsWhileFileIsOpenForWriting()
    {
        using var writer = new FileStream(_file, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        var bytes = Encoding.UTF8.GetBytes("live line\n");
        writer.Write(bytes, 0, bytes.Length);
        writer.Flush();

        Assert.Contains("live line", RunLog.ReadTail(_file));
    }
}
