using System;
using System.IO;
using ScriptDock;
using ScriptDock.Services;
using ScriptDock.Storage;
using Xunit;

namespace ScriptDock.Tests.Services;

public sealed class LogRevealTests
{
    [Fact]
    public void SelectTarget_reveals_the_records_database()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Path);
        var records = Path.Combine(temp.Path, RecordStore.FileName);
        File.WriteAllText(records, "");

        var target = LogReveal.SelectTarget(records, temp.Path, () => { });

        Assert.Equal(LogRevealTargetKind.File, target.Kind);
        Assert.Equal(records, target.Path);
    }

    [Fact]
    public void SelectTarget_flushes_before_looking_for_the_database()
    {
        using var temp = new TempDirectory();
        var records = Path.Combine(temp.Path, RecordStore.FileName);

        var target = LogReveal.SelectTarget(records, temp.Path, () =>
        {
            Directory.CreateDirectory(temp.Path);
            File.WriteAllText(records, "");
        });

        Assert.Equal(LogRevealTargetKind.File, target.Kind);
        Assert.Equal(records, target.Path);
    }

    [Fact]
    public void SelectTarget_opens_the_storage_root_before_the_database_exists()
    {
        using var temp = new TempDirectory();

        var target = LogReveal.SelectTarget(Path.Combine(temp.Path, RecordStore.FileName), temp.Path, () => { });

        Assert.Equal(LogRevealTargetKind.Directory, target.Kind);
        Assert.Equal(temp.Path, target.Path);
    }

    [Fact]
    public void OpenTarget_reports_when_the_shell_did_not_accept_the_request()
    {
        var target = new LogRevealTarget("/tmp/scriptdock.log", LogRevealTargetKind.File);

        var opened = LogReveal.OpenTarget(target, _ => null);

        Assert.False(opened);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "scriptdock-logreveal-tests",
                NanoId.New());
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }
}
