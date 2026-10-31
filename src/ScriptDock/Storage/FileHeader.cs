using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace ScriptDock.Storage;

/// <summary>
/// The identity header every file ScriptDock writes in its own format carries (file-identity-conventions):
/// <c>format</c> says what the file is (owner, kind, version) and <c>writer</c> which build serialized it. Every
/// store writes and checks its header here. A file is accepted or refused on <c>format</c> alone; <c>writer</c>
/// is written on every save and never read.
/// </summary>
/// <remarks>
/// A file whose header is missing, malformed or names another owner or kind is unreadable, with no fallback;
/// one whose version is above <see cref="Version"/> is newer and left untouched (store-recovery-conventions).
/// Run logs, output views and the records fallback log are plain text and carry no header.
/// </remarks>
public static class FileHeader
{
    /// <summary>The product that owns every format here. Never changes once a file exists.</summary>
    public const string Owner = "scriptdock";

    /// <summary>The format version every kind is at. Before the first official release every format stays at 1.</summary>
    public const int Version = 1;

    /// <summary>Each file's kind.</summary>
    public static class Kinds
    {
        /// <summary><c>config.json</c>.</summary>
        public const string Config = "config";

        /// <summary><c>state.json</c>.</summary>
        public const string State = "state";

        /// <summary><c>hidden.json</c>.</summary>
        public const string HiddenScripts = "hiddenScripts";

        /// <summary><c>known-paths.json</c>.</summary>
        public const string KnownPaths = "knownPaths";

        /// <summary><c>records.sqlite3</c>.</summary>
        public const string Records = "records";

        /// <summary><c>backups.sqlite3</c>.</summary>
        public const string Backups = "backups";
    }

    private const string FormatKey = "format";
    private const string WriterKey = "writer";
    private const string Table = "file_header";

    private static readonly AssemblyName Assembly = typeof(FileHeader).Assembly.GetName();

    /// <summary>The writing package, as its project names it.</summary>
    public static string WriterName { get; } = Assembly.Name!;

    /// <summary>The writing package's release version.</summary>
    public static string WriterVersion { get; } = Assembly.Version!.ToString(3);

    /// <summary>Puts the header first in <paramref name="document"/>: <c>format</c>, then <c>writer</c>.</summary>
    public static void WriteTo(JsonObject document, string kind)
    {
        document.Insert(0, FormatKey, new JsonObject { ["owner"] = Owner, ["kind"] = kind, ["version"] = Version });
        document.Insert(1, WriterKey, new JsonObject { ["name"] = WriterName, ["version"] = WriterVersion });
    }

    /// <summary>
    /// Checks <paramref name="document"/>'s header against <paramref name="kind"/> and takes the header out, so
    /// the stored type never sees it. Throws <see cref="JsonException"/> when the file is unreadable and
    /// <see cref="NewerFormatVersionException"/> when a newer build wrote it.
    /// </summary>
    public static void ReadFrom(JsonObject document, string kind, string filePath)
    {
        if (!document.TryGetPropertyValue(FormatKey, out var node) || node is not JsonObject format)
            throw new JsonException("The file has no format header.");
        var owner = Text(format, "owner");
        var foundKind = Text(format, "kind");
        var version = format["version"] is JsonValue value && value.GetValueKind() == JsonValueKind.Number
            && value.TryGetValue<long>(out var number) ? number : (long?)null;
        if (owner is null || foundKind is null || version is null)
            throw new JsonException("The file's format header needs a string owner and kind and an integer version.");
        Check(owner, foundKind, version.Value, kind, filePath, message => new JsonException(message));

        document.Remove(FormatKey);
        document.Remove(WriterKey);
    }

    /// <summary>
    /// Runs on opening a database for writing, before anything else writes to it. A new empty database gets the
    /// header; an existing one must carry ScriptDock's <paramref name="kind"/> at <see cref="Version"/>, and its
    /// writer rows are rewritten. Throws <see cref="NewerFormatVersionException"/> for one a newer build wrote and
    /// <see cref="InvalidDataException"/> for an unreadable one; either is left as it is.
    /// </summary>
    internal static void Open(SqliteConnection connection, string filePath, string kind)
    {
        var version = (long)Scalar(connection, "PRAGMA user_version;")!;
        if (version == 0 && (long)Scalar(connection, "SELECT COUNT(*) FROM sqlite_master;")! == 0)
        {
            using var create = connection.BeginTransaction();
            Execute(connection, create, $"CREATE TABLE {Table} (key TEXT PRIMARY KEY, value TEXT NOT NULL);");
            Put(connection, create, "format.owner", Owner);
            Put(connection, create, "format.kind", kind);
            PutWriter(connection, create);
            Execute(connection, create, $"PRAGMA user_version = {Version};");
            create.Commit();
            return;
        }

        if ((long)Scalar(connection, $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{Table}';")! == 0)
            throw new InvalidDataException($"{filePath} has no {Table} table.");
        var owner = Scalar(connection, $"SELECT value FROM {Table} WHERE key = 'format.owner';") as string;
        var foundKind = Scalar(connection, $"SELECT value FROM {Table} WHERE key = 'format.kind';") as string;
        if (owner is null || foundKind is null)
            throw new InvalidDataException($"{filePath} has no format.owner or format.kind in {Table}.");
        Check(owner, foundKind, version, kind, filePath, message => new InvalidDataException(message));

        using var rewrite = connection.BeginTransaction();
        Execute(connection, rewrite, $"DELETE FROM {Table} WHERE key LIKE 'writer.%';");
        PutWriter(connection, rewrite);
        rewrite.Commit();
    }

    // Owner and kind first: a file of another format is unreadable whatever version it records.
    private static void Check(string owner, string foundKind, long version, string kind, string filePath,
        Func<string, Exception> unreadable)
    {
        if (owner != Owner || foundKind != kind)
            throw unreadable($"{filePath} is a {owner} {foundKind} file, not a {Owner} {kind} file.");
        if (version > Version)
            throw new NewerFormatVersionException(filePath, version, Version);
        if (version != Version)
            throw unreadable($"{filePath} records format version {version}, which this version does not read.");
    }

    private static string? Text(JsonObject format, string key) =>
        format[key] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static void PutWriter(SqliteConnection connection, SqliteTransaction transaction)
    {
        Put(connection, transaction, "writer.name", WriterName);
        Put(connection, transaction, "writer.version", WriterVersion);
    }

    private static void Put(SqliteConnection connection, SqliteTransaction transaction, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO {Table} (key, value) VALUES ($key, $value);";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
