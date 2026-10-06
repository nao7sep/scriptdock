using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ScriptDock.Models;
using ScriptDock.Storage;

namespace ScriptDock.Services;

/// <summary>
/// The process-management surface the view model depends on, extracted from
/// <see cref="ProcessRunner"/> so orchestration (run/stop/restart/dismiss, confirm gating,
/// selection) can be tested with an in-memory fake instead of launching real processes.
/// </summary>
public interface IProcessRunner
{
    /// <summary>Raised when a process is started, restarted, or dismissed.</summary>
    event EventHandler? ProcessesChanged;

    /// <summary>Raised once when a run ends, on whichever thread observed the end.</summary>
    event EventHandler<ScriptProcess>? RunEnded;

    /// <summary>The current set of runs ScriptDock owns (running and finished-but-not-dismissed).</summary>
    IReadOnlyList<ScriptProcess> Active { get; }

    /// <summary>Launches a script. Throws when it could not be started; no run then exists.</summary>
    ScriptProcess Start(string scriptPath);
    Task<bool> TerminateAsync(ScriptProcess handle);

    /// <summary>Stops and dismisses a run, then starts its script afresh: null when the old run did not
    /// stop, and throws as <see cref="Start"/> does when the replacement could not be started.</summary>
    Task<ScriptProcess?> RestartAsync(ScriptProcess handle);
    void Dismiss(ScriptProcess handle);
    Task StopAllAsync();
    void ReconcileExited();
    Task ImportFinishedOutputAsync(IRecordStore records, CancellationToken cancellationToken);
}
