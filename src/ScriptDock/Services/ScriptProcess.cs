using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ScriptDock.Models;

namespace ScriptDock.Services;

/// <summary>
/// One launched script as ScriptDock owns it: its state, exit code, and the path to the
/// run-log file the child writes (read on demand for the console — ScriptDock holds no output pipe
/// to the child). Created and driven by <see cref="ProcessRunner"/>; exposed to the UI as a
/// bindable handle. Exit is finalised exactly once, whether via the process's Exited event,
/// or an explicit <see cref="WaitForExitAsync"/>.
/// </summary>
public sealed class ScriptProcess : IDisposable
{
    private int _finalized;
    private int _disposed;
    private readonly object _lifecycleGate = new();
    private TerminationDisposition _terminationDisposition;
    private bool _exitObserved;
    private readonly SemaphoreSlim _inputGate = new(1, 1);
    private readonly CancellationTokenSource _inputLifetime = new();
    private int _inputDeliveryUncertain;
    private static readonly TimeSpan InputTimeout = TimeSpan.FromSeconds(2);

    public ScriptProcess(int id, string scriptPath, DateTimeOffset startedAt)
        : this(id, scriptPath, startedAt, PathIdentity.Key(scriptPath)) { }

    internal ScriptProcess(int id, string scriptPath, DateTimeOffset startedAt, string scriptKey)
    {
        Id = id;
        ScriptPath = scriptPath;
        ScriptKey = scriptKey;
        StartedAt = startedAt;
    }

    public int Id { get; }
    public string ScriptPath { get; }
    public string ScriptKey { get; }
    public DateTimeOffset StartedAt { get; }
    public RunState State { get; private set; } = RunState.Running;
    public int? ExitCode { get; private set; }
    public bool CanSendInput => State == RunState.Running && Volatile.Read(ref _inputDeliveryUncertain) == 0;

    /// <summary>Path of the file the child shell writes stdout+stderr to; null if the run never started.</summary>
    public string? LogFilePath { get; internal set; }

    /// <summary>OS process id while running, or null if it never started or is already gone.
    /// Recorded with <see cref="OsStartedAt"/> so a later output import can tell whether this exact
    /// run is still alive.</summary>
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

    /// <summary>Sends a line to the running script's stdin, the pipe ScriptDock owns for every run it
    /// starts. No-op unless the run started and is still alive. Never throws.</summary>
    public Task<bool> SendInputAsync(string line) => SendInputAsync(line, InputTimeout);

    internal async Task<bool> SendInputAsync(string line, TimeSpan timeout)
    {
        var process = Process;
        if (process is null)
            return false;

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, _inputLifetime.Token);
        var entered = false;
        var attempted = false;
        try
        {
            await _inputGate.WaitAsync(cts.Token).ConfigureAwait(false);
            entered = true;
            if (!process.HasExited && CanSendInput)
            {
                attempted = true;
                await process.StandardInput.WriteLineAsync(line.AsMemory(), cts.Token).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(cts.Token).ConfigureAwait(false);
                return true;
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation can follow partial delivery and leave the next line joined to its tail.
            // Keep the pipe open, but refuse reuse until the user explicitly restarts this run.
            if (attempted) Interlocked.Exchange(ref _inputDeliveryUncertain, 1);
            Log.Warn("run: send input timed out or was cancelled", new { id = Id });
        }
        catch (Exception ex)
        {
            if (attempted) Interlocked.Exchange(ref _inputDeliveryUncertain, 1);
            Log.Warn("run: send input failed", ex, new { id = Id });
        }
        finally
        {
            if (entered)
                _inputGate.Release();
        }

        return false;
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

    internal bool BeginTerminationAttempt()
    {
        lock (_lifecycleGate)
        {
            if (_finalized == 1)
                return false;
            _terminationDisposition = TerminationDisposition.Pending;
            return true;
        }
    }

    internal void ConfirmTerminationAttempt()
    {
        lock (_lifecycleGate)
        {
            if (_finalized == 1)
                return;
            _terminationDisposition = TerminationDisposition.Confirmed;
            if (!_exitObserved)
                return;
            Finish(RunState.Terminated);
        }

        AnnounceEnd();
    }

    internal void CancelTerminationAttempt()
    {
        lock (_lifecycleGate)
        {
            if (_finalized == 1)
                return;
            _terminationDisposition = TerminationDisposition.None;
            if (!_exitObserved)
                return;
            Finish(RunState.Exited);
        }

        AnnounceEnd();
    }

    internal void Complete()
    {
        lock (_lifecycleGate)
        {
            if (_finalized == 1)
                return;
            if (_terminationDisposition == TerminationDisposition.Pending)
            {
                // The process exited while a kill call or bounded wait was unresolved. Defer the
                // public terminal state until the caller confirms whether that attempt succeeded.
                _exitObserved = true;
                return;
            }
            Finish(_terminationDisposition == TerminationDisposition.Confirmed
                ? RunState.Terminated
                : RunState.Exited);
        }

        AnnounceEnd();
    }

    // Takes the once-only latch and publishes the terminal state together under the lifecycle gate,
    // so a caller that finds the latch already taken (a lost race with the Exited event) returns only
    // once State and ExitCode are final.
    private void Finish(RunState state)
    {
        _finalized = 1;
        try { ExitCode = Process?.ExitCode; }
        catch { /* process state unavailable; leave ExitCode null */ }
        State = state;
    }

    // Outside the gate: cancelling the input lifetime runs its callbacks inline, and StateChanged
    // runs subscriber code.
    private void AnnounceEnd()
    {
        _inputLifetime.Cancel();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Releases the underlying OS process handle and its redirected stdin pipe. Called when
    /// the run leaves ScriptDock's active set (dismiss/restart). The cached <see cref="State"/> and
    /// <see cref="ExitCode"/> and the on-disk run log remain valid afterwards, so a finished run that
    /// is still shown reads correctly; disposing does not terminate the OS process.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _inputLifetime.Cancel();
        Process?.Dispose();
    }

    private enum TerminationDisposition
    {
        None,
        Pending,
        Confirmed,
    }
}
