using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ScriptDock.Storage;
using Xunit;

namespace ScriptDock.Tests.Storage;

/// <summary>
/// Exercises the real file I/O of <see cref="JsonStore{T}"/> against a temp
/// directory redirected via the <c>SCRIPTDOCK_HOME</c> environment variable — the one
/// relocation seam, used the same way in tests and production. These touch the
/// disk on purpose: the atomic write is the behaviour that protects the user's saved
/// data from a torn write, and a fake filesystem would not exercise it. The <c>.bak</c>
/// last-good sidecar has been retired (see the data-backup conventions), so these also
/// assert no such sidecar is ever produced. A self-contained <see cref="SampleDoc"/>
/// stands in for any persisted model, so these tests do not depend on the app's evolving
/// config/state shape.
/// </summary>
[Collection(StorageRootEnvironment.CollectionName)]
public sealed class JsonStoreTests : IDisposable
{
    public enum SampleKind
    {
        FirstChoice,
        SecondChoice,
    }

    public sealed class SampleDoc
    {
        public string Name { get; set; } = "";
        public SampleKind Kind { get; set; }
        public List<string> Items { get; set; } = [];
    }

    private readonly string _root;
    private readonly string? _previousHome;

    public JsonStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "scriptdock-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _previousHome = Environment.GetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable);
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, _previousHome);
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private string PathOf(string fileName) => Path.Combine(_root, fileName);

    [Fact]
    public void SaveThenLoad_RoundTripsValue()
    {
        var store = new JsonStore<SampleDoc>("doc.json", "doc");
        store.Save(new SampleDoc { Name = "x", Kind = SampleKind.SecondChoice, Items = ["a", "b"] });

        var loaded = store.Load();

        Assert.Equal("x", loaded.Name);
        Assert.Equal(SampleKind.SecondChoice, loaded.Kind);
        Assert.Equal(["a", "b"], loaded.Items);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefault()
    {
        var store = new JsonStore<SampleDoc>("doc.json", "doc");

        var loaded = store.Load();

        // Default-constructed SampleDoc.
        Assert.Equal("", loaded.Name);
        Assert.Equal(SampleKind.FirstChoice, loaded.Kind);
        Assert.False(File.Exists(PathOf("doc.json")));
    }

    [Fact]
    public void Load_CorruptPrimary_ReturnsDefault()
    {
        // The .bak sidecar is retired: an unreadable live file falls back to defaults; earlier content is
        // recovered, if ever needed, from the startup backup archives rather than a sidecar.
        var store = new JsonStore<SampleDoc>("doc.json", "doc");
        store.Save(new SampleDoc { Name = "one" });
        store.Save(new SampleDoc { Name = "two" });

        File.WriteAllText(PathOf("doc.json"), "{ not valid json");

        var loaded = store.Load();

        Assert.Equal("", loaded.Name);
        // No sidecar was ever written to recover from.
        Assert.False(File.Exists(PathOf("doc.json.bak")));
    }

    [Fact]
    public void Load_LiteralNullDocument_ReturnsDefault()
    {
        File.WriteAllText(PathOf("doc.json"), "null");
        var store = new JsonStore<SampleDoc>("doc.json", "doc");

        var loaded = store.Load();

        Assert.Equal("", loaded.Name);
        Assert.Equal(SampleKind.FirstChoice, loaded.Kind);
    }

    [Fact]
    public void Save_FirstTime_CreatesLiveFileButNoBakSidecar()
    {
        var store = new JsonStore<SampleDoc>("doc.json", "doc");

        store.Save(new SampleDoc());

        Assert.True(File.Exists(PathOf("doc.json")));
        Assert.False(File.Exists(PathOf("doc.json.bak")));
    }

    [Fact]
    public void Save_SecondTime_WritesNewContentAndStillNoBakSidecar()
    {
        var store = new JsonStore<SampleDoc>("doc.json", "doc");
        store.Save(new SampleDoc { Name = "one" });
        store.Save(new SampleDoc { Name = "two" });

        var liveJson = File.ReadAllText(PathOf("doc.json"));

        Assert.Contains("two", liveJson);
        // The overwrite is atomic (temp + replace) and leaves no last-good sidecar behind.
        Assert.False(File.Exists(PathOf("doc.json.bak")));
    }

    [Fact]
    public void Save_LeavesOnlyTheLiveFileInTheRoot()
    {
        // Locks the retirement: repeated saves produce exactly one file — no .bak, no leftover .tmp.
        var store = new JsonStore<SampleDoc>("doc.json", "doc");
        store.Save(new SampleDoc { Name = "one" });
        store.Save(new SampleDoc { Name = "two" });
        store.Save(new SampleDoc { Name = "three" });

        var files = Directory.EnumerateFiles(_root).Select(Path.GetFileName).ToList();

        Assert.Equal(["doc.json"], files);
    }

    [Fact]
    public void Save_LeavesNoTempFiles()
    {
        var store = new JsonStore<SampleDoc>("doc.json", "doc");
        store.Save(new SampleDoc());
        store.Save(new SampleDoc { Name = "two" });

        var temps = Directory.EnumerateFiles(_root, "*.tmp").ToList();

        Assert.Empty(temps);
    }

    [Fact]
    public void Save_WritesCamelCasePropertiesAndSnakeCaseEnums()
    {
        var store = new JsonStore<SampleDoc>("doc.json", "doc");
        store.Save(new SampleDoc { Name = "x", Kind = SampleKind.SecondChoice });

        var json = File.ReadAllText(PathOf("doc.json"));

        // Locks the on-disk shape so a serializer-option change can't silently
        // orphan existing user files.
        Assert.Contains("\"name\"", json);
        Assert.Contains("\"kind\"", json);
        Assert.Contains("\"second_choice\"", json);
    }
}
