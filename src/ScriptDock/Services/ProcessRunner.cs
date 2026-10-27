using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ScriptDock.Models;
using ScriptDock.Storage;

namespace ScriptDock.Services;

/// <summary>
/// Owns the scripts ScriptDock launches as child processes. Each run goes through a login
/// shell that writes the script's output to a per-run log file (so ScriptDock holds no pipe to
/// the child's output — its own crash can't break the child's writes); the child's stdin is a
/// pipe ScriptDock owns for interactive input, and a crash merely EOFs it. Termination kills the whole process tree (npm/dotnet
/// spawn children) so restart is reliable and ports are freed. A run is owned only by the session that
/// started it; its start (with its PID and OS start-time) and end are recorded by the view model, and the
/// PID and start-time let the output import tell whether a past run's process is still writing.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    // Grace for a process tree to die after a tree-kill. Ownership is retained if the grace
    // expires: starting a replacement while the old tree is alive would create a duplicate.
    private static readonly TimeSpan DefaultTerminationGrace = TimeSpan.FromSeconds(10);

    // How long a finished run's output file stays unchanged before it moves into the records, so output a
    // process the run left behind is still writing is not cut off.
    internal static readonly TimeSpan OutputQuietPeriod = TimeSpan.FromMinutes(1);

    private readonly List<ScriptProcess> _processes = new();
    private readonly object _gate = new();
    private readonly string _runsDirectory;
    private readonly TimeSpan _terminationGrace;
    private readonly Action<Process> _killProcess;
    private int _nextId;
    private bool _launchesSealed;
    private readonly HashSet<string> _admissions = new(PathIdentity.Comparer);
    private readonly List<Task<ScriptProcess>> _launches = new();
    private readonly Dictionary<ScriptProcess, Task<bool>> _terminations = new();
    private readonly Action<ScriptProcess> _startProcess;
    private readonly Func<ScriptProcess, Task<bool>> _terminateProcess;
    private readonly Action<ProcessStartInfo>? _configureStartInfo;

    /// <param name="runsDirectory">Where per-run log files are written; defaults to
    /// <see cref="RunLog.DefaultDirectory"/>. Injected so tests stay isolated.</param>
    public ProcessRunner(string? runsDirectory = null)
        : this(runsDirectory, DefaultTerminationGrace, process => process.Kill(entireProcessTree: true)) { }

    internal ProcessRunner(string? runsDirectory, TimeSpan terminationGrace, Action<Process> killProcess,
        Action<ScriptProcess>? startProcess = null, Func<ScriptProcess, Task<bool>>? terminateProcess = null,
        Action<ProcessStartInfo>? configureStartInfo = null)
    {
        _runsDirectory = runsDirectory ?? RunLog.DefaultDirectory;
        _terminationGrace = terminationGrace;
        _killProcess = killProcess;
        _startProcess = startProcess ?? LaunchProcess;
        _terminateProcess = terminateProcess ?? TerminateProcessAsync;
        _configureStartInfo = configureStartInfo;
    }

    /// <summary>Raised when a process is started, restarted, or dismissed.</summary>
    public event EventHandler? ProcessesChanged;

    /// <summary>Raised once when a run ends, on whichever thread observed the end.</summary>
    public event EventHandler<ScriptProcess>? RunEnded;

    public IReadOnlyList<ScriptProcess> Active
    {
        get { lock (_gate) return _processes.ToList(); }
    }

    public void SealLaunches()
    {
        lock (_gate)
            _launchesSealed = true;
    }

    public async Task<ScriptProcess?> StartAsync(string scriptPath)
    {
        var key = await Task.Run(() => PathIdentity.Key(scriptPath)).ConfigureAwait(false);
        lock (_gate)
        {
            if (_launchesSealed || _processes.Any(p => PathIdentity.Comparer.Equals(p.ScriptKey, key) && p.State == RunState.Running)
                || !_admissions.Add(key))
                return null;
        }
        try
        {
            return await LaunchAsync(scriptPath, key).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
                _admissions.Remove(key);
        }
    }

    private Task<ScriptProcess> LaunchAsync(string scriptPath, string key)
    {
        lock (_gate)
        {
            if (_launchesSealed)
                throw new InvalidOperationException("ScriptDock is stopping.");
            var launch = Task.Run(() =>
            {
                var handle = new ScriptProcess(Interlocked.Increment(ref _nextId), scriptPath, DateTimeOffset.UtcNow, key);
                WatchEnd(handle);
                try
                {
                    _startProcess(handle);
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }

                List<ScriptProcess> stale;
                lock (_gate)
                {
                    stale = _processes.Where(p => p.State != RunState.Running &&
                        PathIdentity.Comparer.Equals(p.ScriptKey, handle.ScriptKey)).ToList();
                    foreach (var process in stale)
                        _processes.Remove(process);
                    _processes.Add(handle);
                }
                foreach (var process in stale)
                    process.Dispose();
                ProcessesChanged?.Invoke(this, EventArgs.Empty);
                return handle;
            });
            _launches.Add(launch);
            return CompleteLaunchAsync(launch);
        }
    }

    private async Task<ScriptProcess> CompleteLaunchAsync(Task<ScriptProcess> launch)
    {
        try { return await launch.ConfigureAwait(false); }
        finally { lock (_gate) _launches.Remove(launch); }
    }

    private void LaunchProcess(ScriptProcess handle)
    {
        var scriptPath = handle.ScriptPath;
        Directory.CreateDirectory(_runsDirectory);
        var logPath = RunLog.PathFor(_runsDirectory, handle.Id, scriptPath, handle.StartedAt);
        handle.LogFilePath = logPath;
        var command = ShellCommand.ForRun(scriptPath, logPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            WorkingDirectory = WorkingDirectoryFor(scriptPath),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
        };
        foreach (var arg in command.Arguments)
            startInfo.ArgumentList.Add(arg);
        _configureStartInfo?.Invoke(startInfo);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Exited += (_, _) => handle.Complete();
        handle.Process = process;
        process.Start();
        Log.Info("run: started", new { id = handle.Id, script = scriptPath, log = logPath });
    }

    public Task<bool> TerminateAsync(ScriptProcess handle)
    {
        lock (_gate)
        {
            if (_terminations.TryGetValue(handle, out var pending))
                return pending;
            var termination = Task.Run(() => _terminateProcess(handle));
            _terminations.Add(handle, termination);
            return CompleteTerminationAsync(handle, termination);
        }
    }

    private async Task<bool> CompleteTerminationAsync(ScriptProcess handle, Task<bool> termination)
    {
        try { return await termination.ConfigureAwait(false); }
        finally { lock (_gate) _terminations.Remove(handle); }
    }

    private async Task<bool> TerminateProcessAsync(ScriptProcess handle)
    {
        var process = handle.Process;
        if (process is null)
            return handle.State != RunState.Running;

        try
        {
            if (process.HasExited)
            {
                handle.Complete();
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("run: could not inspect process before termination", ex, new { id = handle.Id });
            return false;
        }

        if (!handle.BeginTerminationAttempt())
            return handle.State != RunState.Running;
        try
        {
            _killProcess(process);
        }
        catch (Exception ex)
        {
            handle.CancelTerminationAttempt();
            Log.Warn("run: terminate failed", ex, new { id = handle.Id });
            return false;
        }

        var exited = await handle.WaitForExitAsync(_terminationGrace).ConfigureAwait(false);
        if (!exited)
        {
            // The process is still owned and usable. A failed attempt must not poison its stdin
            // channel or make a later natural exit look like a confirmed ScriptDock termination.
            handle.CancelTerminationAttempt();
            Log.Warn("run: termination grace elapsed; retaining ownership", new { id = handle.Id, script = handle.ScriptPath });
            return false;
        }

        handle.ConfirmTerminationAttempt();
        Log.Info("run: terminated", new { id = handle.Id, script = handle.ScriptPath });
        return true;
    }

    /// <summary>Stops the process and launches the same script afresh — the restart primitive. The
    /// old handle is terminated and dismissed; the new one is returned. The wait for the old tree to
    /// die is asynchronous, so a restart never blocks the UI thread even when a child is slow to exit.</summary>
    public async Task<ScriptProcess?> RestartAsync(ScriptProcess handle)
    {
        lock (_gate)
        {
            if (_launchesSealed || !_processes.Contains(handle) || !_admissions.Add(handle.ScriptKey))
                return null;
        }
        try
        {
            if (!await TerminateAsync(handle).ConfigureAwait(false))
                return null;
            lock (_gate)
            {
                if (_launchesSealed)
                    return null;
            }
            Dismiss(handle);
            // LaunchAsync repeats the seal check under the gate that captures pending launches.
            var started = await LaunchAsync(handle.ScriptPath, handle.ScriptKey).ConfigureAwait(false);
            Log.Info("run: restarted", new { oldId = handle.Id, newId = started.Id, script = handle.ScriptPath });
            return started;
        }
        finally
        {
            lock (_gate)
                _admissions.Remove(handle.ScriptKey);
        }
    }

    /// <summary>Removes a (typically finished) process from the active list and releases its OS
    /// handle. The run's cached state/exit code and its on-disk log survive, so a finished run that
    /// is still being shown elsewhere reads correctly.</summary>
    public void Dismiss(ScriptProcess handle)
    {
        bool removed;
        lock (_gate)
            removed = _processes.Remove(handle);

        if (removed)
        {
            handle.Dispose();
            ProcessesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>On quit: terminates every running script's process tree and waits for each to end, all
    /// concurrently and off the calling thread (the tree kill is synchronous), each within the
    /// termination grace. A tree still alive after it is logged and the quit proceeds.</summary>
    public async Task StopAllAsync()
    {
        Task<ScriptProcess>[] launches;
        ScriptProcess[] running;
        lock (_gate)
        {
            _launchesSealed = true;
            launches = _launches.ToArray();
            running = _processes.Where(p => p.State == RunState.Running).ToArray();
        }
        // Stop published runs immediately; a stalled native launch must not hold up their kills.
        var work = running.Select(StopOwnedAsync).Concat(launches.Select(StopLaunchAsync));
        var results = await Task.WhenAll(work).ConfigureAwait(false);
        var alive = results.Where(result => !result.Stopped).Select(result => result.Id).Distinct().ToList();
        if (alive.Count > 0)
            Log.Warn("run: process trees still alive after the quit bound; quitting anyway", new { ids = alive });
        else if (results.Any(result => result.Id != 0))
            Log.Info("run: stopped every running script on quit", new { count = results.Where(result => result.Id != 0).Select(result => result.Id).Distinct().Count() });
    }

    private async Task<(int Id, bool Stopped)> StopOwnedAsync(ScriptProcess handle) =>
        (handle.Id, await TerminateAsync(handle).ConfigureAwait(false));

    private async Task<(int Id, bool Stopped)> StopLaunchAsync(Task<ScriptProcess> launch)
    {
        ScriptProcess handle;
        try { handle = await launch.ConfigureAwait(false); }
        catch { return (0, true); } // the launch's caller owns its failure presentation
        return await StopOwnedAsync(handle).ConfigureAwait(false);
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

    /// <summary>
    /// Moves the output file of each finished run into the records and deletes the file, per the
    /// data-lifecycle-conventions' Records section. A file stays while a run this session holds reads it,
    /// while its run's process (by process id and start time) is alive, while it has changed within
    /// <see cref="OutputQuietPeriod"/>, and when no run record names it. An import replaces any earlier
    /// import of the same run, so a file whose deletion failed is imported again in full on a later pass.
    /// Right before the delete, the file's length and modification time are checked against what was
    /// copied: a process the run left behind may still be writing, so a changed file stays and its next
    /// quiet pass imports it again.
    /// Each file is copied in pieces, so memory stays at one buffer whatever its size, and each is its own
    /// attempt: one that fails stays for a later pass without holding up the rest. A file larger than one
    /// record can hold is known from its size alone and stays where it is as the run's output, unread and not
    /// retried; the Records window reads it from there (volume-handling-conventions).
    /// </summary>
    public Task ImportFinishedOutputAsync(IRecordStore records, CancellationToken cancellationToken)
    {
        // The run list belongs to the caller's dispatcher; transfer a stable snapshot to the worker.
        var held = new HashSet<string>(Active.Select(p => p.LogFilePath).OfType<string>(), PathIdentity.Comparer);
        return Task.Run(() => ImportFinishedOutputCoreAsync(records, held, cancellationToken), cancellationToken);
    }

    private async Task ImportFinishedOutputCoreAsync(IRecordStore records, HashSet<string> held, CancellationToken cancellationToken)
    {
        var quietSince = DateTime.UtcNow - OutputQuietPeriod;
        var candidates = FindQuietOutput(held, quietSince);
        if (candidates.Count == 0)
            return;

        var runs = await records.FindRunsByOutputPathAsync(candidates).ConfigureAwait(false);
        var imported = 0;
        foreach (var path in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!runs.TryGetValue(path, out var run) || IsAlive(run))
                continue;

            try
            {
                var copied = new FileInfo(path);
                var (length, modified) = (copied.Length, copied.LastWriteTimeUtc);
                if (!await ImportOneAsync(records, run, path, length).ConfigureAwait(false))
                    continue;
                copied.Refresh();
                if ((copied.Length, copied.LastWriteTimeUtc) != (length, modified))
                {
                    Log.Info("run output: still being written after the copy; it stays for a later pass", new { path });
                    continue;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn("run output: import failed; the file stays for a later pass", ex, new { path });
                continue;
            }
            imported++;
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                Log.Warn("run output: imported but not deleted; the next pass imports it again", ex, new { path });
            }
        }

        if (imported > 0)
            Log.Info("run output: imported", new { imported, candidates = candidates.Count });
    }

    // A handle's state changes once, when it reaches its terminal state.
    private void WatchEnd(ScriptProcess handle) =>
        handle.StateChanged += (_, _) => RunEnded?.Invoke(this, handle);

    private List<string> FindQuietOutput(HashSet<string> held, DateTime quietSince)
    {
        var paths = new List<string>();
        if (!Directory.Exists(_runsDirectory))
            return paths;
        foreach (var path in Directory.EnumerateFiles(_runsDirectory, "*.log"))
        {
            if (held.Contains(path))
                continue;
            try
            {
                if (File.GetLastWriteTimeUtc(path) < quietSince)
                    paths.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("run output: could not inspect the file; it stays for a later pass", ex, new { path });
            }
        }
        return paths;
    }

    // False when the file is too large for one record and stays as the run's output.
    private static async Task<bool> ImportOneAsync(IRecordStore records, RunRecord run, string path, long length)
    {
        if (length > records.MaxOutputBytes)
            return false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        await records.AddRunOutputAsync(run, stream, length).ConfigureAwait(false);
        return true;
    }

    private static bool IsAlive(RunRecord run)
    {
        if (run.Pid is not { } pid || run.OsStartedAt is not { } osStartedAt)
            return false;

        using var process = TryReattach(pid, osStartedAt);
        return process is not null;
    }

    // Probe a recorded process: return the live process only if its PID exists, has not exited,
    // and its start-time still matches (so a reused PID can't be mistaken for the original).
    private static Process? TryReattach(int pid, DateTimeOffset osStartedAt)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(pid);
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
            if (!StartTimesMatch(osStartedAt, actualStart))
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

    // The run records keep millisecond precision, so accepting a wider window can
    // mistake a quickly reused PID for the process ScriptDock launched.
    internal static bool StartTimesMatch(DateTimeOffset persisted, DateTimeOffset actual) =>
        persisted.ToUnixTimeMilliseconds() == actual.ToUnixTimeMilliseconds();

    // The working directory a launched script runs in: its containing folder. A bare
    // filename (no directory) and a filesystem root both yield "" — Process treats an
    // empty WorkingDirectory as "inherit ScriptDock's current directory", the intended
    // fallback when the script path has no usable parent.
    internal static string WorkingDirectoryFor(string scriptPath) =>
        Path.GetDirectoryName(scriptPath) ?? string.Empty;
}
