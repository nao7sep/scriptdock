using System;
using ScriptDock.Services;

namespace ScriptDock.Models;

/// <summary>
/// One run's end as the records keep it: the run it ends (its session and in-session run id), when the end
/// was seen, how the run ended, and the exit code when the OS gave one. A run is ended only by the session
/// that started it.
/// </summary>
public sealed record RunEnd(string RunSession, int Run, DateTimeOffset EndedAt, string State, int? ExitCode)
{
    public static RunEnd For(string session, ScriptProcess process) =>
        new(session, process.Id, DateTimeOffset.UtcNow, process.State.ToString().ToLowerInvariant(), process.ExitCode);
}
