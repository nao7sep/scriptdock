using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using ScriptDock.Models;

namespace ScriptDock.Services;

/// <summary>
/// One launched script as ScriptDock owns it: its state, exit code, and retained output.
/// Created and driven by <see cref="ProcessRunner"/>; exposed to the UI (Phase 4) as a
/// bindable handle. Exit is finalised exactly once, whether observed via the process's
/// Exited event or an explicit <see cref="WaitForExit"/>.
/// </summary>
public sealed class ScriptProcess
{
    private int _finalized;
    private bool _terminating;

    public ScriptProcess(int id, string scriptPath, DateTimeOffset startedAt)
    {
        Id = id;
        ScriptPath = scriptPath;
        StartedAt = startedAt;
    }

    public int Id { get; }
    public string ScriptPath { get; }
    public DateTimeOffset StartedAt { get; }
    public RunState State { get; private set; } = RunState.Running;
    public int? ExitCode { get; private set; }
    public OutputBuffer Output { get; } = new();

    /// <summary>Raised when <see cref="State"/> changes. May fire on a background thread;
    /// the UI marshals to its dispatcher.</summary>
    public event EventHandler? StateChanged;

    internal Process? Process { get; set; }

    public IReadOnlyList<string> Snapshot() => Output.Snapshot();

    /// <summary>Blocks until the process exits or the timeout elapses; returns whether it
    /// exited. On exit, async output is flushed and the state finalised.</summary>
    public bool WaitForExit(TimeSpan timeout)
    {
        var process = Process;
        if (process is null)
            return true;

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            return false;

        process.WaitForExit(); // flush async output readers
        Complete();
        return true;
    }

    internal void AppendOutput(string? line)
    {
        if (line is not null)
            Output.AppendLine(line);
    }

    internal void MarkTerminating() => _terminating = true;

    internal void Complete()
    {
        if (Interlocked.Exchange(ref _finalized, 1) == 1)
            return;

        try { ExitCode = Process?.ExitCode; }
        catch { /* process state unavailable; leave ExitCode null */ }

        SetState(_terminating ? RunState.Terminated : RunState.Exited);
    }

    internal void Fail(string message)
    {
        AppendOutput(message);
        Interlocked.Exchange(ref _finalized, 1);
        SetState(RunState.Failed);
    }

    private void SetState(RunState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
