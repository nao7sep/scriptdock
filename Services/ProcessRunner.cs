using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace ScriptDock.Services;

/// <summary>
/// Owns the scripts ScriptDock launches as child processes. Each launch goes through a
/// login shell with stdin closed (so a script's "press enter" pause self-clears), its
/// output captured into a retained buffer. Termination kills the whole process tree —
/// npm/dotnet spawn children, and killing only the parent would orphan them and hold
/// ports — which is what makes restart reliable. The active list is runtime-only and
/// never persisted; quitting the app ends these children.
/// </summary>
public sealed class ProcessRunner
{
    private readonly List<ScriptProcess> _processes = new();
    private readonly object _gate = new();
    private int _nextId;

    /// <summary>Raised when a process is started, restarted, or dismissed.</summary>
    public event EventHandler? ProcessesChanged;

    public IReadOnlyList<ScriptProcess> Active
    {
        get { lock (_gate) return _processes.ToList(); }
    }

    public ScriptProcess Start(string scriptPath)
    {
        var id = Interlocked.Increment(ref _nextId);
        var handle = new ScriptProcess(id, scriptPath, DateTimeOffset.UtcNow);

        try
        {
            var command = ShellCommand.For(scriptPath);
            var startInfo = new ProcessStartInfo
            {
                FileName = command.FileName,
                WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? string.Empty,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var arg in command.Arguments)
                startInfo.ArgumentList.Add(arg);

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => handle.AppendOutput(e.Data);
            process.ErrorDataReceived += (_, e) => handle.AppendOutput(e.Data);
            process.Exited += (_, _) => handle.Complete();
            handle.Process = process;

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try { process.StandardInput.Close(); } catch { /* child may already have closed it */ }

            Log.Info("run: started", new { id, script = scriptPath });
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
