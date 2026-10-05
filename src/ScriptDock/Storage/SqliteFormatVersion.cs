using System;
using Microsoft.Data.Sqlite;

namespace ScriptDock.Storage;

/// <summary>
/// A SQLite store's format version, kept in <c>PRAGMA user_version</c> (store-recovery-conventions).
/// </summary>
internal static class SqliteFormatVersion
{
    /// <summary>
    /// Refuses a database newer than <paramref name="supported"/> before anything is written to it, and
    /// returns whether it records no version yet. SQLite's 0, a database that never set one, reads as 1.
    /// </summary>
    public static bool CheckReadable(SqliteConnection connection, string filePath, int supported)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var recorded = Convert.ToInt32(command.ExecuteScalar());
        var found = recorded == 0 ? 1 : recorded;
        if (found > supported)
            throw new NewerFormatVersionException(filePath, found, supported);
        return recorded == 0;
    }

    /// <summary>Records <paramref name="version"/> in a database that has none yet.</summary>
    public static void Record(SqliteConnection connection, int version)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA user_version = {version};";
        command.ExecuteNonQuery();
    }
}
