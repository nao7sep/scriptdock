using System;
using System.Collections.Generic;
using System.Linq;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.ViewModels;
using Xunit;

namespace ScriptDock.Tests.ViewModels;

public sealed class RecentListBuilderTests
{
    private static readonly IReadOnlyDictionary<string, string> NoLabels = new Dictionary<string, string>();

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
        var entries = RecentListBuilder.Build([Run("/b", 5), Run("/a", 1)], [], NoLabels);

        Assert.Equal(["/b", "/a"], entries.Select(e => e.Path));
        Assert.All(entries, e => Assert.Null(e.Process));
        Assert.All(entries, e => Assert.False(e.IsRunning));
    }

    [Fact]
    public void Build_AttachesRunningProcessByPath()
    {
        var running = Running("/a");

        var entry = Assert.Single(RecentListBuilder.Build([Run("/a", 1)], [running], NoLabels));

        Assert.True(entry.IsRunning);
        Assert.Same(running, entry.Process);
        Assert.Equal("Running", entry.StatePillText);
    }

    [Fact]
    public void Build_KeepsStoppedProcess_ForRetainedOutput()
    {
        var stopped = Stopped("/a");

        var entry = Assert.Single(RecentListBuilder.Build([Run("/a", 1)], [stopped], NoLabels));

        Assert.False(entry.IsRunning);
        Assert.Same(stopped, entry.Process);
    }

    [Fact]
    public void Build_PrefersRunningProcess_WhenSamePathHasBoth()
    {
        var stopped = Stopped("/a");
        var running = Running("/a");

        var entry = Assert.Single(RecentListBuilder.Build([Run("/a", 1)], [stopped, running], NoLabels));

        Assert.Same(running, entry.Process);
    }

    [Fact]
    public void Build_SurfacesLiveProcess_AbsentFromTheRecentList()
    {
        // A recaptured run whose recent entry was evicted: present in active, absent from recents.
        var orphan = Running("/recaptured");

        var entries = RecentListBuilder.Build([Run("/a", 5)], [orphan], NoLabels);

        // The recent entry comes first; the un-listed live run is still surfaced and controllable.
        Assert.Equal(["/a", "/recaptured"], entries.Select(e => e.Path));
        var surfaced = entries[1];
        Assert.Same(orphan, surfaced.Process);
        Assert.True(surfaced.IsRunning);
    }

    [Fact]
    public void Build_DoesNotDuplicate_WhenLiveProcessIsAlsoInRecents()
    {
        var running = Running("/a");

        // /a is both a recent and a live process — it must appear exactly once.
        var entries = RecentListBuilder.Build([Run("/a", 1)], [running], NoLabels);

        var entry = Assert.Single(entries);
        Assert.Same(running, entry.Process);
    }
}
