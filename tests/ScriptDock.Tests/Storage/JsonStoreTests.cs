using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ScriptDock.Storage;
using Xunit;

namespace ScriptDock.Tests.Storage;

/// <summary>
/// Exercises the real file I/O of <see cref="JsonStore{T}"/> against a temp
/// directory redirected via <see cref="StorageRoot.Override"/>. These touch the
/// disk on purpose: atomic-write and backup-recovery are the behaviours that
/// protect the user's saved data, and a fake filesystem would not exercise them.
/// A self-contained <see cref="SampleDoc"/> stands in for any persisted model, so
/// these tests do not depend on the app's evolving config/state shape.
/// </summary>
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

    public JsonStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "scriptdock-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        StorageRoot.Override(_root);
    }

    public void Dispose()
    {
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
    public void Load_CorruptPrimary_RecoversFromBackup()
    {
        var store = new JsonStore<SampleDoc>("doc.json", "doc");
        store.Save(new SampleDoc { Name = "one" });
        // A second save promotes the first version into doc.json.bak.
        store.Save(new SampleDoc { Name = "two" });

        File.WriteAllText(PathOf("doc.json"), "{ not valid json");

        var loaded = store.Load();

        // The backup holds the "one" version.
        Assert.Equal("one", loaded.Name);
    }

    [Fact]
    public void Load_PrimaryAndBackupBothCorrupt_ReturnsDefault()
    {
        var store = new JsonStore<SampleDoc>("doc.json", "doc");
        store.Save(new SampleDoc { Name = "one" });

        File.WriteAllText(PathOf("doc.json"), "garbage");
        File.WriteAllText(PathOf("doc.json.bak"), "also garbage");

        var loaded = store.Load();

        Assert.Equal("", loaded.Name);
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
    public void Save_FirstTime_CreatesBothLiveAndBackup()
    {
        var store = new JsonStore<SampleDoc>("doc.json", "doc");

        store.Save(new SampleDoc());

        Assert.True(File.Exists(PathOf("doc.json")));
        Assert.True(File.Exists(PathOf("doc.json.bak")));
    }

    [Fact]
    public void Save_SecondTime_BackupHoldsPreviousVersion()
    {
        var store = new JsonStore<SampleDoc>("doc.json", "doc");
        store.Save(new SampleDoc { Name = "one" });
        store.Save(new SampleDoc { Name = "two" });

        var backupJson = File.ReadAllText(PathOf("doc.json.bak"));
        var liveJson = File.ReadAllText(PathOf("doc.json"));

        Assert.Contains("one", backupJson);
        Assert.Contains("two", liveJson);
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
