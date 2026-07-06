using System;
using System.IO;
using System.Text;
using System.Text.Json;
using ScriptDock.Services;

namespace ScriptDock.Storage;

/// <summary>
/// Generic JSON-backed store with atomic replace. A single file is written:
/// <c>{file}</c> is the live document, replaced by a write-to-temp-then-rename so a
/// crash mid-write never tears it. If the live document is missing, the type's
/// default-constructed value is returned. If it exists but will not parse, it is
/// quarantined (moved aside, bytes preserved) and the default-constructed value is
/// returned in its place — see <see cref="TryLoadFile"/>.
/// </summary>
/// <remarks>
/// The <c>.bak</c> last-good sidecar this store once kept has been retired (see the
/// data-backup conventions): the atomic write below is the durability floor against a
/// torn write, and the point-in-time history a user can be recovered from now lives in
/// the write-through <see cref="BackupStore"/> (<c>~/.scriptdock/backups.sqlite3</c>),
/// which this store feeds strictly after each rename lands. An unreadable live file is
/// never left in place for a later <see cref="Save"/> to silently overwrite — the
/// storage-path conventions' forbidden path — so it is quarantined aside first and
/// defaults proceed; the live file reappears the next time it is (re)created.
/// </remarks>
/// <remarks>
/// Caller responsibilities: this store does not impose any ordering or
/// canonicalisation on the value it receives. If on-disk ordering matters
/// (for diff stability or hand-editing), the caller must sort before
/// calling <see cref="Save"/> — and should sort a copy rather than the
/// live in-memory collection to avoid surprising callers that share it.
/// </remarks>
public sealed class JsonStore<T> : IJsonStore<T> where T : class, new()
{
    private readonly string _filePath;
    private readonly string _label;

    /// <summary>
    /// Creates a store rooted at <see cref="StorageRoot.Directory"/>.
    /// </summary>
    /// <param name="fileName">File name (no directory component), e.g. <c>"config.json"</c>.</param>
    /// <param name="label">Human-readable noun used in log messages, e.g. <c>"config"</c>.</param>
    public JsonStore(string fileName, string label)
    {
        _filePath = Path.Combine(StorageRoot.Directory, fileName);
        _label = label;
    }

    /// <summary>
    /// True when the live document already exists on disk. Distinguishes a first run
    /// ("seed defaults") from a document the user has deliberately emptied, which
    /// <see cref="Load"/> alone cannot tell apart.
    /// </summary>
    public bool Exists => File.Exists(_filePath);

    public T Load()
    {
        if (TryLoadFile(_filePath, out var value))
            return value;

        // Reached on first run (no file yet — normal) or after the live file was
        // unreadable (already logged a warn above).
        Log.Info("store: no existing data, using defaults", new { label = _label });
        return new T();
    }

    public void Save(T value)
    {
        try
        {
            StorageRoot.EnsureExists();
            var json = JsonSerializer.Serialize(value, JsonOptions.Default);
            // Encode once to raw bytes and write those exact bytes, so the copy the backup records is
            // byte-identical to what lands on disk (no re-encode, no BOM surprise). UTF-8 without a BOM,
            // matching File.WriteAllText's default so the on-disk shape is unchanged from before.
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json);
            WriteAtomically(bytes);
            Log.Info("store: saved", new { label = _label, path = _filePath });
        }
        catch (Exception ex)
        {
            Log.Error("store: save failed", ex, new { label = _label, path = _filePath });
            throw;
        }
    }

    private bool TryLoadFile(string filePath, out T value)
    {
        value = new T();

        // An absent file is normal (first run): not a failure, so it is not logged
        // here — the caller decides what the absence means.
        if (!File.Exists(filePath))
            return false;

        try
        {
            var json = File.ReadAllText(filePath);
            value = JsonSerializer.Deserialize<T>(json, JsonOptions.Default) ?? new T();
            Log.Info("store: loaded", new { label = _label, path = filePath });
            return true;
        }
        catch (Exception ex)
        {
            // The file exists but will not parse — unexpected, yet recoverable. Per the
            // storage-path conventions, a present-but-corrupt managed file is quarantined
            // (moved aside, never left for a later Save to silently overwrite) rather than
            // discarded in place; defaults proceed from here.
            Quarantine(filePath, ex);
            return false;
        }
    }

    // Moves the unreadable file aside to <stem>-<millisecond-utc-stamp>.invalid, in the same
    // directory, per the derived-filename grammar — a plain rename, so the original bytes are
    // preserved exactly rather than copied or rewritten. One warning names both the original and
    // the quarantine path. If the move itself cannot complete (e.g. a permission error, a
    // colliding name), the load failure is still warned so it is never silently lost, and the
    // corrupt file is left where it was rather than risking a half-done move.
    private void Quarantine(string filePath, Exception loadException)
    {
        var directory = Path.GetDirectoryName(filePath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(filePath);
        var quarantinePath = Path.Combine(
            directory, $"{stem}-{TimestampConventions.FileStampMillis(DateTimeOffset.UtcNow)}.invalid");

        try
        {
            File.Move(filePath, quarantinePath);
            Log.Warn("store: file unreadable, quarantined", loadException,
                new { label = _label, path = filePath, quarantine = quarantinePath });
        }
        catch (Exception moveEx)
        {
            Log.Warn("store: file unreadable, could not quarantine", moveEx,
                new { label = _label, path = filePath, loadError = loadException.Message });
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
    // same buffer already in hand, never a re-read of the file. The record is best-effort and silent; it
    // never throws back into this write and never affects the save's success (see BackupStore.Record).
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
                File.Replace(tempPath, _filePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, _filePath);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }

        // After the rename: the file is exactly where it belongs, so record the bytes we just wrote.
        // Best-effort — Record catches, logs once, and swallows every failure, so a backup problem can
        // never break the save that already succeeded above.
        BackupStore.Record(_filePath, bytes);
    }
}
