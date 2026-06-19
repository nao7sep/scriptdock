using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using ScriptDock.Models;

namespace ScriptDock.Services;

/// <summary>
/// One launched script as ScriptDock owns it: its state, exit code, and the path to the
/// run-log file the child writes (read on demand for the console — ScriptDock holds no pipe
/// to the child). Created and driven by <see cref="ProcessRunner"/>; exposed to the UI as a
/// bindable handle. Exit is finalised exactly once, whether via the process's Exited event
/// or an explicit <see cref="WaitForExit"/>.
/// </summary>
public sealed class ScriptProcess
{
    private int _finalized;
    private bool _terminating;
    private string? _failureMessage;

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

    /// <summary>Path of the file the child shell writes stdout+stderr to; null if the run never started.</summary>
    public string? LogFilePath { get; internal set; }

    /// <summary>OS process id while running, or null if it never started or is already gone.
    /// Persisted with <see cref="OsStartedAt"/> so a relaunch can recapture this exact run.</summary>
    public int? Pid
    {
        get { try { return Process?.Id; } catch { return null; } }
    }

    /// <summary>OS process start time in UTC, or null if unavailable. Paired with <see cref="Pid"/>
    /// to recognise this process after a restart — a reused PID has a different start time.</summary>
    public DateTimeOffset? OsStartedAt
    {
        get
        {
            try
            {
                return Process is { } process && !process.HasExited
                    ? new DateTimeOffset(process.StartTime).ToUniversalTime()
                    : null;
            }
            catch { return null; }
        }
    }

    /// <summary>Raised when <see cref="State"/> changes. May fire on a background thread;
    /// the UI marshals to its dispatcher.</summary>
    public event EventHandler? StateChanged;

    internal Process? Process { get; set; }

    /// <summary>The tail of this run's output (ANSI-stripped), read from the run-log file.</summary>
    public IReadOnlyList<string> ReadOutput()
    {
        if (_failureMessage is not null)
            return [_failureMessage];

        return LogFilePath is null ? Array.Empty<string>() : RunLog.ReadTail(LogFilePath);
    }

    /// <summary>Blocks until the process exits or the timeout elapses; returns whether it
    /// exited. On exit, the state is finalised.</summary>
    public bool WaitForExit(TimeSpan timeout)
    {
        var process = Process;
        if (process is null)
            return true;

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            return false;

        Complete();
        return true;
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
        _failureMessage = message;
        Interlocked.Exchange(ref _finalized, 1);
        SetState(RunState.Failed);
    }

    private void SetState(RunState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
