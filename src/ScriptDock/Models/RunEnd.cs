using System;
using ScriptDock.Services;

namespace ScriptDock.Models;

/// <summary>
/// One run's end as the records keep it: the run it ends (its session and in-session run id), when the end
/// was seen, how the run ended, and the exit code when the OS gave one. A run whose process a relaunch found
/// no longer running was never seen to end, so its end says <see cref="Gone"/> at the time it was found.
/// </summary>
public sealed record RunEnd(string RunSession, int Run, DateTimeOffset EndedAt, string State, int? ExitCode)
{
    public const string Gone = "gone";

    public static RunEnd For(string session, ScriptProcess process) =>
        new(
            process.Recaptured?.Session ?? session,
            process.Recaptured?.Run ?? process.Id,
            DateTimeOffset.UtcNow,
            process.State.ToString().ToLowerInvariant(),
            process.ExitCode);

    public static RunEnd GoneOf(RunRecord run) => new(run.Session, run.Run, DateTimeOffset.UtcNow, Gone, null);
}
