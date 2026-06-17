using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace ScriptDock.Services;

/// <summary>
/// Owns the scripts ScriptDock launches as child processes. Each run goes through a login
/// shell that writes the script's output to a per-run log file and reads stdin from
/// <c>/dev/null</c> — so ScriptDock holds no pipe to the child, and its own crash leaves the
/// child no broken pipe to die on. Termination kills the whole process tree (npm/dotnet
/// spawn children) so restart is reliable and ports are freed. The active list is
/// runtime-only and never persisted; quitting the app ends these children.
/// </summary>
public sealed class ProcessRunner
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
                // No stdout/stderr/stdin redirection: the child shell sends output to the run
                // log and reads stdin from /dev/null, so we hold no pipe that a crash could break.
            };
            foreach (var arg in command.Arguments)
                startInfo.ArgumentList.Add(arg);

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Exited += (_, _) => handle.Complete();
            handle.Process = process;

            process.Start();
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

    /// <summary>Terminates everything still running — called on app shutdown.</summary>
    public void ShutdownAll()
    {
        foreach (var handle in Active)
            Terminate(handle);
    }
}
