using System;
using System.IO;
using System.Text;
using ScriptDock;
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
        _dir = Path.Combine(Path.GetTempPath(), "scriptdock-runlog-tests", NanoId.New());
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

    [Fact]
    public void TailReadDoesNotFollowBytesAppendedAfterItsSnapshot()
    {
        File.WriteAllText(_file, "old\nlast\n");
        using var stream = new AppendOnReadStream(_file);
        Assert.Equal(["last"], RunLog.ReadTail(stream, 6));
        Assert.True(new FileInfo(_file).Length > 6);
    }

    [MacOnlyFact]
    public void OutputRetriesAnUnreadableFileWithoutWaitingForItToGrow()
    {
        if (!OperatingSystem.IsMacOS()) return;
        File.WriteAllText(_file, "visible\n");
        using var process = new ScriptProcess(1, _file, default) { LogFilePath = _file };
        var mode = File.GetUnixFileMode(_file);
        try
        {
            File.SetUnixFileMode(_file, UnixFileMode.None);
            Assert.Empty(process.ReadOutput());
        }
        finally { File.SetUnixFileMode(_file, mode); }
        Assert.Equal(["visible"], process.ReadOutput());
    }

    private sealed class AppendOnReadStream(string path) : Stream
    {
        private readonly FileStream _file = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        private bool _appended;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!_appended)
            {
                _appended = true;
                File.AppendAllText(path, new string('x', 10000) + "\nnew\n");
            }
            return _file.Read(buffer, offset, count);
        }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _file.Length;
        public override long Position { get => _file.Position; set => _file.Position = value; }
        public override long Seek(long offset, SeekOrigin origin) => _file.Seek(offset, origin);
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _file.Dispose(); base.Dispose(disposing); }
    }

    [Fact]
    public void ReadTail_CollapsesCarriageReturnProgressUpdates()
    {
        File.WriteAllText(_file, "Building 1\rBuilding 2\rBuilding 3\n");

        Assert.Equal(["Building 3"], RunLog.ReadTail(_file));
    }

    [Fact]
    public void ReadTail_KeepsFinishedLines_CollapsesTrailingProgress()
    {
        File.WriteAllText(_file, "Compiling a\nBuilding 1\rBuilding 2\rCompiling b\nBuilding 3\rBuilding 4");

        Assert.Equal(["Compiling a", "Compiling b", "Building 4"], RunLog.ReadTail(_file));
    }
}
