using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using ScriptDock;
using ScriptDock.Storage;
using Xunit;

namespace ScriptDock.Tests.Storage;

/// <summary>
/// Pins the write-through data-backup store (<see cref="BackupStore"/>) against a throwaway
/// <c>SCRIPTDOCK_HOME</c> — the one relocation seam, used the same way in tests and production. These
/// touch a real SQLite file on purpose: the whole point of the feature is a byte-identical on-disk copy,
/// which a fake would not exercise. What is locked here: the <c>content</c> BLOB is byte-identical
/// (including a CR/LF and a non-UTF-8 byte, proving it is raw bytes and not decoded text);
/// <c>written_at_utc</c> is the serialized ISO-8601-ms data value and NOT the filename stamp; dedup skips
/// an unchanged re-save while a changed save and a revert each insert a row; and the store is best-effort
/// — an injected open failure produces no throw and leaves the caller's save untouched.
/// </summary>
[Collection(StorageRootEnvironment.CollectionName)]
public sealed class BackupStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string? _previousHome;

    public BackupStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "scriptdock-tests", NanoId.New());
        Directory.CreateDirectory(_root);

        _previousHome = Environment.GetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable);
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, _root);
    }

    public void Dispose()
    {
        // Release the singleton's handle on this throwaway root's store before the directory is deleted,
        // and reset it so the next test re-opens against its own SCRIPTDOCK_HOME.
        BackupStore.Close();
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, _previousHome);
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private string StoreFile => Path.Combine(_root, BackupStore.FileName);

    private sealed record Row(string Path, byte[] Content, string Sha256, long ByteSize, string WrittenAtUtc);

    /// <summary>Reads every recorded row for a path, oldest first — a direct DB read so the assertion does
    /// not depend on any public read API the feature deliberately does not expose.</summary>
    private List<Row> RowsFor(string path)
    {
        var rows = new List<Row>();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = StoreFile,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT path, content, content_sha256, byte_size, written_at_utc " +
            "FROM backups WHERE path = $path ORDER BY id ASC";
        command.Parameters.AddWithValue("$path", path);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var content = (byte[])reader["content"];
            rows.Add(new Row(
                reader.GetString(0),
                content,
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetString(4)));
        }

        return rows;
    }

    [Fact]
    public void Record_StoresContentByteIdentical_IncludingCrLfAndNonUtf8()
    {
        var path = Path.Combine(_root, "doc.json");
        // A CRLF, a UTF-8 BOM, and a raw 0xFF byte that is not valid UTF-8: if the store decoded to text
        // anywhere, the CRLF would normalize, the BOM would drop or shift, and 0xFF would corrupt.
        byte[] bytes = [0xEF, 0xBB, 0xBF, (byte)'a', (byte)'\r', (byte)'\n', (byte)'b', 0xFF, 0x00, (byte)'c'];

        BackupStore.Record(path, bytes);

        var rows = RowsFor(path);
        Assert.Single(rows);
        Assert.Equal(bytes, rows[0].Content); // byte-for-byte, no normalization
        Assert.Equal(bytes.LongLength, rows[0].ByteSize);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), rows[0].Sha256);
        Assert.Equal(path, rows[0].Path);
    }

    [Fact]
    public void Record_WrittenAtUtc_IsSerializedIsoMillis_NotAFilenameStamp()
    {
        var path = Path.Combine(_root, "doc.json");
        BackupStore.Record(path, [1, 2, 3]);

        var writtenAt = RowsFor(path)[0].WrittenAtUtc;

        // The serialized ISO-8601-ms shape: 2026-07-06T04:05:12.345Z.
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", writtenAt);
        // And explicitly NOT the yyyymmdd-hhmmss-fff-utc filename stamp.
        Assert.DoesNotMatch(@"^\d{8}-\d{6}-\d{3}-utc$", writtenAt);
        Assert.DoesNotContain("-utc", writtenAt);
        // It round-trips as a real instant.
        Assert.True(DateTimeOffset.TryParse(writtenAt, out _));
    }

    [Fact]
    public void Record_UnchangedResave_IsDeduped_NoNewRow()
    {
        var path = Path.Combine(_root, "doc.json");
        byte[] bytes = [10, 20, 30];

        BackupStore.Record(path, bytes);
        BackupStore.Record(path, bytes); // identical content — must be skipped
        BackupStore.Record(path, bytes); // still identical

        Assert.Single(RowsFor(path));
    }

    [Fact]
    public void Record_ChangedSave_InsertsANewRow()
    {
        var path = Path.Combine(_root, "doc.json");

        BackupStore.Record(path, [1]);
        BackupStore.Record(path, [1, 2]); // different content

        var rows = RowsFor(path);
        Assert.Equal(2, rows.Count);
        Assert.Equal([1], rows[0].Content);
        Assert.Equal([1, 2], rows[1].Content);
    }

    [Fact]
    public void Record_Revert_InsertsANewRow_DifferingFromTheImmediatelyPrecedingRow()
    {
        var path = Path.Combine(_root, "doc.json");
        byte[] original = [1, 1, 1];
        byte[] edited = [2, 2, 2];

        BackupStore.Record(path, original); // v1
        BackupStore.Record(path, edited);   // v2 — a real edit
        BackupStore.Record(path, original); // revert to v1's content: differs from v2, so recorded

        var rows = RowsFor(path);
        Assert.Equal(3, rows.Count);
        Assert.Equal(original, rows[2].Content);
    }

    [Fact]
    public void Record_PerPathDedup_TracksEachPathIndependently()
    {
        var a = Path.Combine(_root, "a.json");
        var b = Path.Combine(_root, "b.json");
        byte[] same = [7, 7, 7];

        // Same content under two different paths must record once per path (dedup is per path, not global).
        BackupStore.Record(a, same);
        BackupStore.Record(b, same);
        BackupStore.Record(a, same); // dedup skip for a
        BackupStore.Record(b, same); // dedup skip for b

        Assert.Single(RowsFor(a));
        Assert.Single(RowsFor(b));
    }

    public sealed class SampleDoc
    {
        public string Name { get; set; } = "";
    }

    [Fact]
    public void JsonStoreSave_RecordsThroughTheChokePoint_AfterTheRenameLands()
    {
        // The end-to-end wire: a managed-text save through JsonStore's single atomic-write choke point
        // records the exact bytes it wrote, at the file's full absolute path, strictly after the rename.
        var store = new JsonStore<SampleDoc>("doc.json", "doc");
        var docPath = Path.Combine(_root, "doc.json");

        store.Save(new SampleDoc { Name = "one" });

        var onDisk = File.ReadAllBytes(docPath);
        var rows = RowsFor(docPath);
        Assert.Single(rows);
        Assert.Equal(onDisk, rows[0].Content); // recorded bytes are byte-identical to what landed on disk
        Assert.Equal(docPath, rows[0].Path);   // full absolute path
    }

    [Fact]
    public void JsonStoreSave_RepeatedIdenticalSaves_Dedup_ButAChangeRecordsANewVersion()
    {
        var store = new JsonStore<SampleDoc>("doc.json", "doc");
        var docPath = Path.Combine(_root, "doc.json");

        store.Save(new SampleDoc { Name = "one" });
        store.Save(new SampleDoc { Name = "one" }); // no real change — deduped
        store.Save(new SampleDoc { Name = "two" }); // a real change — recorded

        Assert.Equal(2, RowsFor(docPath).Count);
    }

    [Fact]
    public void Record_WhenStoreCannotOpen_DoesNotThrow_AndTheSaveIsUnaffected()
    {
        // Inject an open failure: put a *file* where the storage root's directory must be, so the store's
        // Directory.CreateDirectory / open cannot succeed against this SCRIPTDOCK_HOME.
        var blockedRoot = Path.Combine(Path.GetTempPath(), "scriptdock-tests", NanoId.New() + "-blocked");
        File.WriteAllText(blockedRoot, "not a directory");
        var previous = Environment.GetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable);
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, blockedRoot);
        BackupStore.Close(); // force a re-open against the blocked root

        try
        {
            // The record itself must be best-effort: swallow the failure, never throw, so a caller's save
            // (which has already landed on disk before Record is reached) is never broken by the backup.
            var exception = Record.Exception(() => BackupStore.Record(Path.Combine(blockedRoot, "doc.json"), [1, 2, 3]));
            Assert.Null(exception);

            // A second record after the open already failed is a silent no-op (disabled for the session),
            // not a re-throw and not a re-open attempt.
            Assert.Null(Record.Exception(() => BackupStore.Record(Path.Combine(blockedRoot, "doc.json"), [4, 5, 6])));
        }
        finally
        {
            BackupStore.Close();
            Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, previous);
            try { File.Delete(blockedRoot); } catch { /* best-effort */ }
        }
    }
}
