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
/// bindable handle. Every change happens on the runner's thread. Exit is finalised exactly once, whether
/// via the process's Exited event, posted to that thread, or an explicit <see cref="WaitForExitAsync"/>.
/// </summary>
public sealed class ScriptProcess : IDisposable
{
    private int _finalized;
    private bool _disposed;
    private readonly CancellationTokenSource _inputLifetime = new();
    private bool _inputDeliveryUncertain;
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
    public bool CanSendInput => State == RunState.Running && !_inputDeliveryUncertain;

    /// <summary>The runner has killed this run's tree and waits for it to end, so its end is a termination.</summary>
    internal bool StopRequested { get; set; }

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

    /// <summary>Raised once, on the runner's thread, when <see cref="State"/> reaches its terminal value.</summary>
    public event EventHandler? StateChanged;

    internal Process? Process { get; set; }

    /// <summary>Sends a line to the running script's stdin, the pipe ScriptDock owns for every run it
    /// starts. No-op unless the run started and is still alive. Never throws.</summary>
    public Task<bool> SendInputAsync(string line) => SendInputAsync(line, InputTimeout);

    internal async Task<bool> SendInputAsync(string line, TimeSpan timeout)
    {
        var process = Process;
        if (process is null || process.HasExited || !CanSendInput)
            return false;

        // One submission at a time: the console input sends the next line only after this one settles.
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, _inputLifetime.Token);
        try
        {
            await process.StandardInput.WriteLineAsync(line.AsMemory(), cts.Token);
            await process.StandardInput.FlushAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            // Cancellation can follow partial delivery and leave the next line joined to its tail.
            // Keep the pipe open, but refuse reuse until the user explicitly restarts this run.
            _inputDeliveryUncertain = true;
            Log.Warn("run: send input timed out or was cancelled", new { id = Id });
        }
        catch (Exception ex)
        {
            _inputDeliveryUncertain = true;
            Log.Warn("run: send input failed", ex, new { id = Id });
        }

        return false;
    }

    /// <summary>Asynchronously waits until the process exits or the timeout elapses; returns whether
    /// it exited. On exit, the state is finalised. Never blocks the calling thread, so the restart
    /// path keeps the interface responsive even when a process tree is slow to die.</summary>
    public async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        var process = Process;
        if (process is null)
            return true;

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return false; // still alive when the grace elapsed
        }

        Complete();
        return true;
    }

    // The end, whichever of the Exited event and a wait sees it first: Terminated when the runner killed it.
    internal void Complete()
    {
        if (Interlocked.Exchange(ref _finalized, 1) == 1)
            return;
        try { ExitCode = Process?.ExitCode; }
        catch { /* process state unavailable; leave ExitCode null */ }
        State = StopRequested ? RunState.Terminated : RunState.Exited;
        _inputLifetime.Cancel();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Releases the underlying OS process handle and its redirected stdin pipe. Called when
    /// the run leaves ScriptDock's active set (dismiss/restart). The cached <see cref="State"/> and
    /// <see cref="ExitCode"/> and the on-disk run log remain valid afterwards, so a finished run that
    /// is still shown reads correctly; disposing does not terminate the OS process.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _inputLifetime.Cancel();
        Process?.Dispose();
    }
}
