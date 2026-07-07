using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ScriptDock.Models;

namespace ScriptDock.Services;

/// <summary>
/// One launched script as ScriptDock owns it: its state, exit code, and the path to the
/// run-log file the child writes (read on demand for the console — ScriptDock holds no pipe
/// to the child). Created and driven by <see cref="ProcessRunner"/>; exposed to the UI as a
/// bindable handle. Exit is finalised exactly once, whether via the process's Exited event,
/// an explicit <see cref="WaitForExit"/>, or <see cref="WaitForExitAsync"/>.
/// </summary>
public sealed class ScriptProcess : IDisposable
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

    /// <summary>Whether this run accepts stdin input from the app. True only for runs this session
    /// started (which own a redirected stdin pipe); a recaptured run has no input channel.</summary>
    public bool AcceptsInput { get; internal set; }

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

    private long _outputCacheLength = long.MinValue;
    private IReadOnlyList<string> _outputCache = Array.Empty<string>();

    /// <summary>
    /// The tail of this run's output (ANSI-stripped), read from the run-log file. A run log is
    /// append-only for the life of a run (the shell truncates once at start, then only appends), so
    /// the file's length is a sound change key: the tail is re-read and re-parsed only when the file
    /// has grown since the last call — otherwise the cached lines are returned. This keeps the
    /// periodic output poll from re-reading and re-stripping a large tail on every tick of a log that
    /// hasn't changed.
    /// </summary>
    public IReadOnlyList<string> ReadOutput()
    {
        if (_failureMessage is not null)
            return [_failureMessage];

        if (LogFilePath is null)
            return Array.Empty<string>();

        long length;
        try { length = new FileInfo(LogFilePath).Length; }
        catch { length = -1; } // missing/unreadable — ReadTail returns empty for the same reason

        if (length == _outputCacheLength)
            return _outputCache;

        _outputCache = RunLog.ReadTail(LogFilePath);
        _outputCacheLength = length;
        return _outputCache;
    }

    /// <summary>Sends a line to the running script's stdin. No-op unless this run accepts input
    /// (see <see cref="AcceptsInput"/>) and is still alive. Never throws.</summary>
    public void SendInput(string line)
    {
        var process = Process;
        if (process is null || !AcceptsInput)
            return;

        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.WriteLine(line);
                process.StandardInput.Flush();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("run: send input failed", ex, new { id = Id });
        }
    }

    /// <summary>Blocks until the process exits or the timeout elapses; returns whether it
    /// exited. On exit, the state is finalised. For UI-thread callers prefer
    /// <see cref="WaitForExitAsync"/>, which does not block.</summary>
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

    /// <summary>Asynchronously waits until the process exits or the timeout elapses; returns whether
    /// it exited. On exit, the state is finalised. Never blocks the calling thread, so the restart
    /// path stays off the UI thread even when a process tree is slow to die.</summary>
    public async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        var process = Process;
        if (process is null)
            return true;

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false; // still alive when the grace elapsed
        }

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
        // Honour the once-only finalisation latch, exactly as Complete does: a run is finalised
        // once, so a Fail after the process has already Exited (or vice versa) cannot overwrite the
        // real terminal state or re-raise StateChanged.
        if (Interlocked.Exchange(ref _finalized, 1) == 1)
            return;

        _failureMessage = message;
        SetState(RunState.Failed);
    }

    private void SetState(RunState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Releases the underlying OS process handle and its redirected stdin pipe. Called when
    /// the run leaves ScriptDock's active set (dismiss/restart). The cached <see cref="State"/> and
    /// <see cref="ExitCode"/> and the on-disk run log remain valid afterwards, so a finished run that
    /// is still shown reads correctly; disposing does not terminate the OS process.</summary>
    public void Dispose() => Process?.Dispose();
}
