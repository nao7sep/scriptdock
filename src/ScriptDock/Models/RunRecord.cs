using System;
using ScriptDock.Services;

namespace ScriptDock.Models;

/// <summary>
/// One started run as the records keep it: the session and in-session run id that name it, when and what it
/// ran, the OS identity (process id and start time) by which a later pass tells whether it is still alive,
/// and the file its output was written to. A run that never started has no process id or start time.
/// </summary>
public sealed record RunRecord(
    string Session,
    int Run,
    DateTimeOffset StartedAt,
    string ScriptPath,
    int? Pid,
    DateTimeOffset? OsStartedAt,
    string? OutputPath)
{
    public static RunRecord For(string session, ScriptProcess process) =>
        new(session, process.Id, process.StartedAt, process.ScriptPath, process.Pid, process.OsStartedAt, process.LogFilePath);
}
