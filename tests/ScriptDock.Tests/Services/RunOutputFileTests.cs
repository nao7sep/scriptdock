using System;
using System.IO;
using System.Linq;
using System.Text;
using ScriptDock.Services;
using Xunit;

namespace ScriptDock.Tests.Services;

/// <summary>
/// The file Open full output hands to the editor: the whole stored output as the console shows it, one file
/// per run under the storage root, replaced on a reopen, and earlier sessions' files cleared by the first write.
/// </summary>
public sealed class RunOutputFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scriptdock-output-view-tests", NanoId.New());

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    [Fact]
    public void Writes_the_whole_output_as_the_console_shows_it_and_replaces_it_on_a_reopen()
    {
        var directory = Path.Combine(_root, RunOutputFile.DirectoryName);
        Directory.CreateDirectory(directory);
        var earlier = Path.Combine(directory, "left-from-an-earlier-session-7.txt");
        File.WriteAllText(earlier, "old");

        var large = new string('x', 3 * 1024 * 1024);
        var path = RunOutputFile.Write(_root, "/repo/dev server.command", 42,
            Encoding.UTF8.GetBytes("\u001b[31mboom\u001b[0m\r\nprogress 1\rprogress 9\n" + large + "\nfar end\n"));

        Assert.Equal(Path.Combine(directory, "dev server-42.txt"), path);
        Assert.Equal("boom\nprogress 9\n" + large + "\nfar end\n", File.ReadAllText(path));
        Assert.False(File.Exists(earlier));

        var other = RunOutputFile.Write(_root, "/repo/build.sh", 43, Encoding.UTF8.GetBytes("built\n"));
        RunOutputFile.Write(_root, "/repo/dev server.command", 42, Encoding.UTF8.GetBytes("again\n"));

        Assert.Equal("again\n", File.ReadAllText(path));
        Assert.Equal("built\n", File.ReadAllText(other));
        Assert.Equal(
            new[] { "build-43.txt", "dev server-42.txt" },
            Directory.GetFiles(directory).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }
}
