using System;
using System.Linq;
using System.IO;
using ScriptDock.Models;
using ScriptDock.Services;
using Xunit;

namespace ScriptDock.Tests.Services;

public sealed class RecentRunsTests
{
    [MacOnlyFact]
    public void Add_DeduplicatesPhysicalSymlinkAliases()
    {
        var root = Path.Combine(Path.GetTempPath(), "scriptdock-recent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var physical = Directory.CreateDirectory(Path.Combine(root, "physical"));
            var script = Path.Combine(physical.FullName, "run.command");
            File.WriteAllText(script, "exit 0");
            var alias = Path.Combine(root, "alias");
            Directory.CreateSymbolicLink(alias, physical.FullName);
            var oldAlias = Path.Combine(alias, "run.command");

            var result = RecentRuns.Add(
                [new RecentRun { Path = oldAlias, RanAt = DateTimeOffset.UtcNow.AddMinutes(-1) }],
                script,
                DateTimeOffset.UtcNow);

            Assert.Single(result);
            Assert.Equal(script, result[0].Path);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static RecentRun Run(string path, int minute) =>
        new() { Path = path, RanAt = new DateTimeOffset(2026, 6, 17, 0, minute, 0, TimeSpan.Zero) };

    [Fact]
    public void Add_PutsNewestFirst()
    {
        var list = RecentRuns.Add([Run("/a", 1)], "/b", new DateTimeOffset(2026, 6, 17, 0, 5, 0, TimeSpan.Zero));

        Assert.Equal("/b", list[0].Path);
        Assert.Equal("/a", list[1].Path);
    }

    [Fact]
    public void Add_DeduplicatesSamePath_MovingItToFront()
    {
        var list = RecentRuns.Add([Run("/a", 1), Run("/b", 2)], "/a", new DateTimeOffset(2026, 6, 17, 0, 9, 0, TimeSpan.Zero));

        Assert.Equal(2, list.Count);
        Assert.Equal("/a", list[0].Path);
        Assert.Equal("/b", list[1].Path);
    }

    [Fact]
    public void Add_CapsAtMax()
    {
        var existing = Enumerable.Range(0, 5).Select(i => Run($"/p{i}", i)).ToList();

        var list = RecentRuns.Add(existing, "/new", new DateTimeOffset(2026, 6, 17, 0, 30, 0, TimeSpan.Zero), max: 3);

        Assert.Equal(3, list.Count);
        Assert.Equal("/new", list[0].Path);
    }
}
