using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using ScriptDock.Models;

namespace ScriptDock.Services;

/// <summary>
/// Owns the scripts ScriptDock launches as child processes. Each run goes through a login
/// shell that writes the script's output to a per-run log file (so ScriptDock holds no pipe to
/// the child's output — its own crash can't break the child's writes); the child's stdin is a
/// pipe ScriptDock owns for interactive input, and a crash merely EOFs it. Termination kills the whole process tree (npm/dotnet
/// spawn children) so restart is reliable and ports are freed. The running set is persisted by
/// the view model (PID + OS start-time) so a relaunch can <see cref="Recapture"/> still-running
/// children; whether quitting kills them is configurable (default: leave them running).
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    private readonly List<ScriptProcess> _processes = new();
    private readonly object _gate = new();
    private readonly string _runsDirectory;
    private int _nextId;

    /// <param name="runsDirectory">Where per-run log files are written; defaults to
    /// <see cref="RunLog.DefaultDirectory"/>. Injected so tests stay isolated.</param>
    public ProcessRunner(string? runsDirectory = null) =>
        _runsDirectory = runsDirectory ?? RunLog.DefaultDirectory;

    /// <summary>Raised when a process is started, restarted, or dismissed.</summary>
    public event EventHandler? ProcessesChanged;

    public IReadOnlyList<ScriptProcess> Active
    {
        get { lock (_gate) return _processes.ToList(); }
    }

    public ScriptProcess Start(string scriptPath)
    {
        var id = Interlocked.Increment(ref _nextId);
        var startedAt = DateTimeOffset.UtcNow;
        var handle = new ScriptProcess(id, scriptPath, startedAt);

        try
        {
            Directory.CreateDirectory(_runsDirectory);
            var logPath = RunLog.PathFor(_runsDirectory, id, scriptPath, startedAt);
            handle.LogFilePath = logPath;

            var command = ShellCommand.ForRun(scriptPath, logPath);
            var startInfo = new ProcessStartInfo
            {
                FileName = command.FileName,
                WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? string.Empty,
                UseShellExecute = false,
                CreateNoWindow = true,
                // stdout/stderr are NOT redirected — the child shell writes them to the run log, so a
                // crash can't break the child's output. stdin IS redirected so the user can type into an
                // interactive script; a crash merely EOFs it, which the child handles like a closed terminal.
                RedirectStandardInput = true,
            };
            foreach (var arg in command.Arguments)
                startInfo.ArgumentList.Add(arg);

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Exited += (_, _) => handle.Complete();
            handle.Process = process;

            process.Start();
            handle.AcceptsInput = true; // we own this run's stdin pipe (a recaptured run does not)
            Log.Info("run: started", new { id, script = scriptPath, log = logPath });
        }
        catch (Exception ex)
        {
            handle.Fail($"Failed to start: {ex.Message}");
            Log.Error("run: start failed", ex, new { script = scriptPath });
        }

        lock (_gate)
            _processes.Add(handle);
        ProcessesChanged?.Invoke(this, EventArgs.Empty);

        return handle;
    }

    public void Terminate(ScriptProcess handle)
    {
        handle.MarkTerminating();

        var process = handle.Process;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                Log.Warn("run: terminate failed", ex, new { id = handle.Id });
            }
        }

        Log.Info("run: terminated", new { id = handle.Id, script = handle.ScriptPath });
    }

    /// <summary>Stops the process and launches the same script afresh — the restart
    /// primitive. The old handle is dismissed; the new one is returned.</summary>
    public ScriptProcess Restart(ScriptProcess handle, TimeSpan? grace = null)
    {
        Log.Info("run: restarted", new { id = handle.Id, script = handle.ScriptPath });
        Terminate(handle);
        handle.WaitForExit(grace ?? TimeSpan.FromSeconds(10));
        Dismiss(handle);
        return Start(handle.ScriptPath);
    }

    /// <summary>Removes a (typically finished) process from the active list.</summary>
    public void Dismiss(ScriptProcess handle)
    {
        bool removed;
        lock (_gate)
            removed = _processes.Remove(handle);

        if (removed)
            ProcessesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>On app shutdown: terminate everything still running when <paramref name="kill"/> is
    /// set; otherwise leave the children running (they detach and can be recaptured next launch).</summary>
    public void ShutdownAll(bool kill)
    {
        if (!kill)
        {
            Log.Info("run: leaving children running on close", new { running = Active.Count(p => p.State == RunState.Running) });
            return;
        }

        foreach (var handle in Active)
            Terminate(handle);
    }

    /// <summary>
    /// Re-attaches to scripts a previous session left running, matched by PID and OS start-time.
    /// A persisted PID that is gone, has exited, or whose start-time no longer matches (a reused
    /// PID) is treated as no longer running and skipped. Re-attached handles raise <c>Exited</c>
    /// and can be tree-killed exactly like ones this session started; the console reads their
    /// existing run-log.
    /// </summary>
    public void Recapture(IReadOnlyList<PersistedProcess> records)
    {
        var recaptured = 0;
        foreach (var record in records)
        {
            var process = TryReattach(record);
            if (process is null)
                continue;

            var id = Interlocked.Increment(ref _nextId);
            var handle = new ScriptProcess(id, record.ScriptPath, record.LaunchedAt) { LogFilePath = record.LogFilePath };
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => handle.Complete();
            handle.Process = process;

            // Close the gap between the probe and the subscribe: if it exited just now, finalise.
            if (process.HasExited)
                handle.Complete();

            lock (_gate)
                _processes.Add(handle);
            recaptured++;
            Log.Info("run: recaptured", new { id, pid = record.Pid, script = record.ScriptPath });
        }

        if (recaptured > 0)
            ProcessesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Backstop for a missed <c>Exited</c> event: finalise any process the OS has ended
    /// whose state still reads Running. Read-only on the handles; cheap to call on a timer.</summary>
    public void ReconcileExited()
    {
        foreach (var handle in Active)
        {
            try
            {
                if (handle.State == RunState.Running && handle.Process is { HasExited: true })
                    handle.Complete();
            }
            catch { /* handle unavailable; nothing to reconcile */ }
        }
    }

    // Probe a persisted record: return the live process only if its PID exists, has not exited,
    // and its start-time still matches (so a reused PID can't be mistaken for the original).
    private static Process? TryReattach(PersistedProcess record)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(record.Pid);
        }
        catch
        {
            return null; // no live process with that PID
        }

        try
        {
            if (process.HasExited)
            {
                process.Dispose();
                return null;
            }

            var actualStart = new DateTimeOffset(process.StartTime).ToUniversalTime();
            if (!StartTimesMatch(record.OsStartedAt, actualStart))
            {
                process.Dispose(); // PID was reused by an unrelated process
                return null;
            }

            return process;
        }
        catch
        {
            try { process.Dispose(); } catch { /* best effort */ }
            return null;
        }
    }

    // Two records identify the same OS process when their start-times agree within a small
    // tolerance (clock/precision slack); a reused PID will differ by far more.
    internal static bool StartTimesMatch(DateTimeOffset persisted, DateTimeOffset actual) =>
        (actual - persisted).Duration() <= TimeSpan.FromSeconds(2);
}
