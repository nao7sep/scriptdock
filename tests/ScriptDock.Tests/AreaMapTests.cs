using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace ScriptDock.Tests;

/// <summary>
/// tests/README.md carries the balance judgement the tests-folder conventions require: which areas
/// ScriptDock has, and which tests stand for each. A map nobody checks decays into a list of paths
/// that used to exist, so these read the real file and hold it to what is on disk.
/// </summary>
public class AreaMapTests
{
    private sealed record Area(string Name, string What, IReadOnlyList<string> Tests);

    [Fact]
    public void the_map_names_areas()
    {
        Assert.True(Read().Count > 1);
    }

    [Fact]
    public void every_area_says_what_it_covers_and_names_a_test()
    {
        var empty = Read()
            .Where(area => area.What.Length == 0 || area.Tests.Count == 0)
            .Select(area => area.Name)
            .ToArray();

        Assert.Empty(empty);
    }

    [Fact]
    public void every_named_test_exists()
    {
        var missing = Read()
            .SelectMany(area => area.Tests.Select(test => (area.Name, test)))
            .Where(named => !File.Exists(Path.Combine(TestsDirectory(), named.test.Replace('/', Path.DirectorySeparatorChar))))
            .Select(named => $"{named.Name}: {named.test}")
            .ToArray();

        Assert.Empty(missing);
    }

    /// <summary>The table's rows, past its header row and the row of dashes under it.</summary>
    private static List<Area> Read()
    {
        var rows = File.ReadAllLines(Path.Combine(TestsDirectory(), "README.md"))
            .Where(line => line.StartsWith('|'))
            .Skip(2);

        return rows.Select(row =>
        {
            var cells = row.Split('|');
            return new Area(
                cells.ElementAtOrDefault(1)?.Trim() ?? string.Empty,
                cells.ElementAtOrDefault(2)?.Trim() ?? string.Empty,
                Regex.Matches(cells.ElementAtOrDefault(3) ?? string.Empty, "`([^`]+)`")
                    .Select(match => match.Groups[1].Value)
                    .ToArray());
        }).ToList();
    }

    /// <summary>The tests tree, from this file's own path: &lt;repo&gt;/tests/ScriptDock.Tests/AreaMapTests.cs.</summary>
    private static string TestsDirectory([CallerFilePath] string callerPath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(callerPath)!, ".."));
}
