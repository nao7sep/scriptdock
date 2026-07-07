using System;
using System.IO;

namespace ScriptDock.Services;

/// <summary>
/// Naming and creation for per-launch log files: one fresh file per process
/// launch, named strictly <c>yyyymmdd-hhmmss-fff-utc.log</c> — the UTC session-start
/// timestamp with millisecond precision and nothing else, no uniqueness suffix. Two
/// launches in the same UTC millisecond collide on the name; the exclusive create then
/// fails and the caller (see <c>Log.Start</c>) degrades to console logging, rather than
/// the collision being engineered around.
/// </summary>
public static class SessionLog
{
    /// <summary>
    /// The log file name for a launch at <paramref name="timestamp"/>. The instant
    /// is converted to UTC (via the shared <see cref="TimestampConventions.FileStampMillis"/>),
    /// so the name does not depend on the local time zone.
    /// </summary>
    public static string FileName(DateTimeOffset timestamp) =>
        TimestampConventions.FileStampMillis(timestamp) + ".log";

    /// <summary>The full path of the session log for a launch instant, under <paramref name="logsDirectory"/>.</summary>
    public static string PathFor(string logsDirectory, DateTimeOffset timestamp) =>
        Path.Combine(logsDirectory, FileName(timestamp));

    /// <summary>
    /// Opens the fresh log file for a real process launch.
    /// </summary>
    public static StreamWriter OpenWriter(string logsDirectory) =>
        OpenWriter(logsDirectory, DateTimeOffset.UtcNow);

    /// <summary>
    /// Opens a fresh log file for the specified launch instant. This overload is
    /// deterministic for tests and tools that need to assert the physical file name.
    /// </summary>
    public static StreamWriter OpenWriter(string logsDirectory, DateTimeOffset timestamp)
    {
        Directory.CreateDirectory(logsDirectory);

        var stream = new FileStream(PathFor(logsDirectory, timestamp), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        return new StreamWriter(stream) { AutoFlush = false };
    }
}
