using System.IO;
using Microsoft.Data.Sqlite;

namespace ScriptDock.Storage;

/// <summary>
/// A SQLite store's format version, kept in <c>PRAGMA user_version</c> (store-recovery-conventions).
/// </summary>
internal static class SqliteFormatVersion
{
    /// <summary>
    /// Runs before anything else writes to the database. A database without a version, a brand-new one
    /// included, is the current format and is stamped with <paramref name="current"/>. One a newer version
    /// wrote is refused with <see cref="NewerFormatVersionException"/>, and any other value with
    /// <see cref="InvalidDataException"/>; either is left as it is.
    /// </summary>
    public static void Check(SqliteConnection connection, string filePath, int current)
    {
        var found = Scalar(connection, "PRAGMA user_version;");
        if (found > current)
            throw new NewerFormatVersionException(filePath, (int)found, current);
        if (found == current)
            return;
        if (found != 0)
            throw new InvalidDataException($"{filePath} records format {found}, which this version does not read.");

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
