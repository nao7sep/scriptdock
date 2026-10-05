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
    /// <paramref name="current"/>, and refuses one that has tables but no version (unreadable) or a
    /// version newer than <paramref name="current"/>.
    /// </summary>
    public static void Check(SqliteConnection connection, string filePath, int current)
    {
        var found = Scalar(connection, "PRAGMA user_version;");
        if (found > current)
            throw new NewerFormatVersionException(filePath, (int)found, current);
        if (found != 0)
            return;
        if (Scalar(connection, "SELECT COUNT(*) FROM sqlite_master;") != 0)
            throw new InvalidDataException($"{filePath} records no format version.");

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
