using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ScriptDock.Models;

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

    /// <summary>The current set of runs ScriptDock owns (running and finished-but-not-dismissed).</summary>
    IReadOnlyList<ScriptProcess> Active { get; }

    ScriptProcess Start(string scriptPath);
    void Terminate(ScriptProcess handle);
    Task<ScriptProcess> RestartAsync(ScriptProcess handle);
    void Dismiss(ScriptProcess handle);
    void ShutdownAll(bool kill);
    void Recapture(IReadOnlyList<PersistedProcess> records);
    void ReconcileExited();
}
