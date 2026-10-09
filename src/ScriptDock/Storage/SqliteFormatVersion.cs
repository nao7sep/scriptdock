using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace ScriptDock.Storage;

/// <summary>
/// A SQLite store's format version, kept in <c>PRAGMA user_version</c> (store-recovery-conventions).
/// </summary>
internal static class SqliteFormatVersion
{
    /// <summary>
    /// Runs before anything else writes to the database: stamps a brand-new, empty one with
    /// <paramref name="current"/>, refuses one a newer version wrote, and accepts one without a version
    /// only when every table in it is one of <paramref name="knownTables"/>.
    /// </summary>
    /// <remarks>
    /// Builds before 2026-10-05, v0.1.0 included, wrote these stores without a version. Each of their
    /// schemas is a subset of format 1 that the store's own <c>CREATE TABLE IF NOT EXISTS</c> completes,
    /// so such a database is format 1 and is stamped as it. A table the store does not know means some
    /// other content, which stays unreadable rather than being guessed at.
    /// </remarks>
    public static void Check(SqliteConnection connection, string filePath, int current, IReadOnlySet<string> knownTables)
    {
        var found = Scalar(connection, "PRAGMA user_version;");
        if (found > current)
            throw new NewerFormatVersionException(filePath, (int)found, current);
        if (found != 0)
            return;

        using (var tables = connection.CreateCommand())
        {
            tables.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
            using var reader = tables.ExecuteReader();
            while (reader.Read())
            {
                if (!knownTables.Contains(reader.GetString(0)))
                    throw new InvalidDataException($"{filePath} records no format version and holds a table this version does not know.");
            }
        }

        using var stamp = connection.CreateCommand();
        stamp.CommandText = $"PRAGMA user_version = {current};";
        stamp.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }
}
