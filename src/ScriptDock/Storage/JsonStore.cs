using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ScriptDock.Services;

namespace ScriptDock.Storage;

/// <summary>
/// Generic JSON-backed store with atomic replace. A single file is written:
/// <c>{file}</c> is the live document, replaced by a write-to-temp-then-rename so a
/// crash mid-write never tears it, and is not written at all when its bytes already match the
/// file on disk, so an unchanged save leaves the file and its backups alone. If the live
/// document is missing, the type's default-constructed value is returned. If it exists but will not parse, it is
/// quarantined (moved aside, bytes preserved) and the default-constructed value is
/// returned in its place — see <see cref="TryLoadFile"/>; a rebuildable store's unreadable
/// file is only logged, and its next save replaces it (store-recovery-conventions).
/// </summary>
/// <remarks>
/// The store owns its format version (store-recovery-conventions): every write puts it first in the
/// document as <c>formatVersion</c>, and a read takes it out before the document is deserialized, so
/// the stored type never sees it. A file without it was written by a build before 2026-10-05 (v0.1.0
/// included) in a shape format 1 still reads, so it reads as format 1. A file recording a newer version
/// than the store's is refused with <see cref="NewerFormatVersionException"/> and left exactly in place,
/// except in a rebuildable store, whose disposable contents are rebuilt like any unreadable file.
/// </remarks>
/// <remarks>
/// The app's single managed-text atomic-write choke point, and so the one place the
/// data-backup hook lives: each recorded store hands <see cref="BackupStore"/>
/// (<c>~/.scriptdock/backups.sqlite3</c>) its bytes strictly after its rename lands.
/// </remarks>
/// <remarks>
/// The store imposes no ordering on the value it receives. If on-disk ordering
/// matters (diff stability, hand-editing), the caller sorts a copy before
/// <see cref="SaveAsync"/>.
/// </remarks>
/// <remarks>
/// <see cref="SaveAsync"/> queues each write on a single chained task per store, so writes to one
/// store are serialized (one at a time, never interleaved) and land strictly in the order they were
/// queued — a write queued from an earlier call can never overwrite one queued from a later call,
/// regardless of how long either takes on a slow disk. The document itself is serialized to bytes
/// synchronously, on the calling thread, before it is queued: callers hold one shared mutable
/// document and mutate it in place, so the snapshot for a given call must be taken at the moment of
/// the call, not whenever its turn in the queue happens to arrive.
/// </remarks>
public sealed class JsonStore<T> : IJsonStore<T> where T : class, new()
{
    private const string FormatVersionKey = "formatVersion";

    private readonly string _filePath;
    private readonly string _label;
    private readonly int _formatVersion;
    private readonly bool _recordBackups;
    private readonly bool _rebuildable;

    // The queue's tail: the next write chains onto this so writes run one at a time, strictly in
    // the order they were queued. Guarded by _queueGate since callers may queue from any thread.
    private readonly object _queueGate = new();
    private Task _queueTail = Task.CompletedTask;

    /// <summary>
    /// Creates a store rooted at <see cref="StorageRoot.Directory"/>.
    /// </summary>
    /// <param name="fileName">File name (no directory component), e.g. <c>"config.json"</c>.</param>
    /// <param name="label">Human-readable noun used in log messages, e.g. <c>"config"</c>.</param>
    /// <param name="formatVersion">The format version this build writes and reads up to, from <see cref="FormatVersions"/>.</param>
    /// <param name="recordBackups">False for volatile state; durable text is recorded by default.</param>
    /// <param name="rebuildable">True for a store the app rebuilds on its own, whose unreadable or newer-version file is not preserved.</param>
    public JsonStore(string fileName, string label, int formatVersion, bool recordBackups = true, bool rebuildable = false)
    {
        _filePath = Path.Combine(StorageRoot.Directory, fileName);
        _label = label;
        _formatVersion = formatVersion;
        _recordBackups = recordBackups;
        _rebuildable = rebuildable;
    }

    public T Load()
    {
        if (TryLoadFile(_filePath, out var value))
            return value;

        // Reached on first run (no file yet — normal) or after the live file was
        // unreadable (already logged a warn above).
        Log.Info("store: no existing data, using defaults", new { label = _label });
        return new T();
    }

    public Task SaveAsync(T value)
    {
        // Cheap, in-memory, CPU-only work: takes the exact snapshot the caller intends, right now,
        // before the caller can mutate the shared document any further. Only the disk write below is
        // queued off the calling thread.
        var document = JsonSerializer.SerializeToNode(value, JsonOptions.Default) as JsonObject
            ?? throw new InvalidOperationException($"The {_label} store's document is not a JSON object.");
        document.Insert(0, FormatVersionKey, _formatVersion);
        var json = document.ToJsonString(JsonOptions.Default);
        // Encode once to raw bytes and write those exact bytes, so the copy the backup records is
        // byte-identical to what lands on disk (no re-encode, no BOM surprise). UTF-8 without a BOM,
        // matching File.WriteAllText's default so the on-disk shape is unchanged from before.
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json);

        lock (_queueGate)
        {
            _queueTail = ContinueQueue(_queueTail, bytes);
            return _queueTail;
        }
    }

    // Awaits the previous write (swallowing its failure — the call that queued it already observes
    // that failure on its own returned task) before running this write on a background thread, so
    // the queue never runs two writes to the same file at once and never runs them out of order.
    private async Task ContinueQueue(Task previous, byte[] bytes)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch
        {
            // Already surfaced to whoever queued that earlier write; this write proceeds regardless.
        }

        await Task.Run(() =>
        {
            try
            {
                // A write that changes nothing is skipped (content-lifecycle-conventions).
                if (MatchesFile(bytes))
                    return;
                StorageRoot.EnsureExists();
                WriteAtomically(bytes);
                Log.Info("store: saved", new { label = _label, path = _filePath });
            }
            catch (Exception ex)
            {
                Log.Error("store: save failed", ex, new { label = _label, path = _filePath });
                throw;
            }
        }).ConfigureAwait(false);
    }

    // Compared at write time against what is on disk, not against the last value saved, so an edit
    // made to the file from outside is still replaced by the app's own content.
    private bool MatchesFile(byte[] bytes) =>
        File.Exists(_filePath) && File.ReadAllBytes(_filePath).AsSpan().SequenceEqual(bytes);

    private bool TryLoadFile(string filePath, out T value)
    {
        value = new T();

        // An absent file is normal (first run): not a failure, so it is not logged
        // here — the caller decides what the absence means.
        if (!File.Exists(filePath))
            return false;

        try
        {
            var document = JsonNode.Parse(File.ReadAllText(filePath)) as JsonObject
                ?? throw new JsonException("The document is not a JSON object.");
            var found = FormatVersionOf(document);
            if (found > _formatVersion)
                throw new NewerFormatVersionException(filePath, found, _formatVersion);
            document.Remove(FormatVersionKey);
            value = document.Deserialize<T>(JsonOptions.Default)!;
            if (value is IJsonNormalizable normalizable)
                normalizable.NormalizeAfterLoad();
            Log.Info("store: loaded", new { label = _label, path = filePath });
            return true;
        }
        catch (NewerFormatVersionException ex) when (!_rebuildable)
        {
            // Intact data a newer build wrote: never quarantined, rebuilt or written to.
            Log.Warn("store: written by a newer version, left in place", ex,
                new { label = _label, path = filePath, found = ex.Found, supported = ex.Supported });
            throw;
        }
        catch (Exception ex) when (_rebuildable)
        {
            // Disposable contents, a newer version's included: defaults now, replaced on the next save.
            Log.Warn("store: file unreadable, rebuilding", ex, new { label = _label, path = filePath });
            return false;
        }
        catch (Exception ex)
        {
            // Present but unparseable: quarantine aside (bytes preserved) before defaults proceed.
            Quarantine(filePath, ex);
            return false;
        }
    }

    // A missing marker is a pre-marker file, format 1; one that is present but not a positive integer
    // makes the file unreadable.
    private static int FormatVersionOf(JsonObject document)
    {
        if (!document.TryGetPropertyValue(FormatVersionKey, out var marker))
            return 1;
        return marker is JsonValue value && value.TryGetValue<int>(out var version) && version >= 1
            ? version
            : throw new JsonException($"{FormatVersionKey} is not a positive integer.");
    }

    // Moves the unreadable file aside to its timestamped same-directory .invalid name, preserving
    // the bytes, and logs one warning naming both paths. The move either lands or its failure
    // propagates — falling through to defaults with the corrupt file still in place would let the
    // next Save overwrite the very bytes quarantine exists to preserve.
    private void Quarantine(string filePath, Exception loadException)
    {
        var directory = Path.GetDirectoryName(filePath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(filePath);
        var quarantinePath = Path.Combine(
            directory, $"{stem}-{TimestampConventions.FileStamp(DateTimeOffset.UtcNow)}.invalid");

        try
        {
            File.Move(filePath, quarantinePath);
            Log.Warn("store: file unreadable, quarantined", loadException,
                new { label = _label, path = filePath, quarantine = quarantinePath });
            QuarantineJournal.Record(quarantinePath);
        }
        catch (Exception moveEx)
        {
            Log.Warn("store: file unreadable, could not quarantine", moveEx,
                new { label = _label, path = filePath, loadError = loadException.Message });
            throw new QuarantineFailedException(filePath, moveEx);
        }
    }

    // The single managed-text atomic-write choke point (data-backup conventions). Write-to-temp-then-
    // rename: the temp holds the full new content before it ever replaces the live file, so a crash
    // mid-write leaves the old file intact rather than a torn one. No .bak sidecar is produced —
    // File.Replace's backup argument is null and the first-write path is a plain move (see the class
    // remarks). A managed-text write that bypasses this helper is a silent backup gap; there is
    // deliberately no second atomic-write path for the app's own managed text.
    //
    // The data-backup record fires strictly AFTER the rename lands. Recording before the rename would
    // risk a "backup of a save that never happened": if the rename then failed, the history would hold a
    // version that never reached disk. So: rename lands, *then* record the exact bytes just written — the
    // same buffer already in hand, never a re-read of the file. Record only queues the bytes for the
    // history's own thread, so the history never delays this save or affects its success.
    private void WriteAtomically(byte[] bytes)
    {
        // <stem>-<discriminator>.tmp, in the same directory as the live file — per the
        // derived-filename grammar, never a suffix dot-appended after the full file name.
        var directory = Path.GetDirectoryName(_filePath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(_filePath);
        var tempPath = Path.Combine(directory, $"{stem}-{NanoId.New()}.tmp");

        try
        {
            File.WriteAllBytes(tempPath, bytes);

            if (File.Exists(_filePath))
            {
                if (OperatingSystem.IsMacOS())
                    File.SetUnixFileMode(tempPath, File.GetUnixFileMode(_filePath));
                File.Replace(tempPath, _filePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, _filePath);
            }
        }
        finally
        {
            // A temp file left behind is harmless; failing to remove it must not hide the write's own failure.
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch (Exception ex)
            {
                Log.Warn("store: could not remove a temp file", ex, new { label = _label, path = tempPath });
            }
        }

        // After the rename: the file is exactly where it belongs, so record the bytes we just wrote.
        if (_recordBackups)
            BackupStore.Record(_filePath, bytes);
    }
}
