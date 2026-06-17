using System;
using System.Linq;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.ViewModels;
using Xunit;

namespace ScriptDock.Tests.ViewModels;

public sealed class DockListBuilderTests
{
    private static RecentRun Run(string path, int minute) =>
        new() { Path = path, RanAt = new DateTimeOffset(2026, 6, 17, 0, minute, 0, TimeSpan.Zero) };

    private static ScriptProcess Running(string path) => new(1, path, default);

    private static ScriptProcess Stopped(string path)
    {
        var process = new ScriptProcess(2, path, default);
        process.Complete(); // no live Process → Exited
        return process;
    }

    [Fact]
    public void Build_OneEntryPerRecent_NewestFirst_NoProcesses()
    {
        var entries = DockListBuilder.Build([Run("/b", 5), Run("/a", 1)], []);

        Assert.Equal(["/b", "/a"], entries.Select(e => e.Path));
        Assert.All(entries, e => Assert.Null(e.Process));
        Assert.All(entries, e => Assert.False(e.IsRunning));
    }

    [Fact]
    public void Build_AttachesRunningProcessByPath()
    {
        var running = Running("/a");

        var entry = Assert.Single(DockListBuilder.Build([Run("/a", 1)], [running]));

        Assert.True(entry.IsRunning);
        Assert.Same(running, entry.Process);
        Assert.Equal("running", entry.StateText);
    }

    [Fact]
    public void Build_KeepsStoppedProcess_ForRetainedOutput()
    {
        var stopped = Stopped("/a");

        var entry = Assert.Single(DockListBuilder.Build([Run("/a", 1)], [stopped]));

        Assert.False(entry.IsRunning);
        Assert.Same(stopped, entry.Process);
    }

    [Fact]
    public void Build_PrefersRunningProcess_WhenSamePathHasBoth()
    {
        var stopped = Stopped("/a");
        var running = Running("/a");

        var entry = Assert.Single(DockListBuilder.Build([Run("/a", 1)], [stopped, running]));

        Assert.Same(running, entry.Process);
    }
}
