using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.Storage;

namespace ScriptDock.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IProcessRunner"/> that records calls and serves a controllable Active set, so
/// view-model orchestration (confirm gating, selection) is testable without launching real processes
/// or needing a Dispatcher. <see cref="ProcessesChanged"/> is a no-op event (empty accessors) so it is
/// never raised — the VM's command paths rebuild directly, keeping tests off the UI thread.
/// </summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    private readonly List<ScriptProcess> _active = new();
    private int _nextId = 1;

    public List<string> StartCalls { get; } = new();
    public List<ScriptProcess> TerminateCalls { get; } = new();
    public List<ScriptProcess> RestartCalls { get; } = new();
    public List<ScriptProcess> DismissCalls { get; } = new();
    public List<RunRecord> RecaptureCalls { get; } = new();
    public bool TerminateResult { get; set; } = true;
    public bool RestartResult { get; set; } = true;
    public Exception? RecaptureException { get; set; }

    /// <summary>When set, <see cref="RestartAsync"/> awaits it before completing — lets a test hold a
    /// restart "in flight" long enough to exercise a re-entrancy guard, the way the real 10-second
    /// termination grace does.</summary>
    public TaskCompletionSource? RestartGate { get; set; }

    public event EventHandler? ProcessesChanged { add { } remove { } }

    public event EventHandler<ScriptProcess>? RunEnded;

    public IReadOnlyList<ScriptProcess> Active => _active;

    /// <summary>Arrange a running process for a path directly (a ScriptProcess is Running on creation).
    /// <paramref name="acceptsInput"/> mirrors the real runner: a run this session started owns a
    /// stdin pipe (true); a recaptured run does not (false, the default for this arrange helper).</summary>
    public ScriptProcess AddRunning(string scriptPath, bool acceptsInput = false)
    {
        var process = new ScriptProcess(_nextId++, scriptPath, DateTimeOffset.UtcNow) { AcceptsInput = acceptsInput };
        Add(process);
        return process;
    }

    public ScriptProcess Start(string scriptPath)
    {
        StartCalls.Add(scriptPath);
        return AddRunning(scriptPath, acceptsInput: true); // the real Start owns the run's stdin pipe
    }

    public Task<bool> TerminateAsync(ScriptProcess handle)
    {
        TerminateCalls.Add(handle);
        return Task.FromResult(TerminateResult);
    }

    public async Task<ScriptProcess?> RestartAsync(ScriptProcess handle)
    {
        RestartCalls.Add(handle);
        if (RestartGate is not null)
            await RestartGate.Task;
        if (!RestartResult)
            return null;
        _active.Remove(handle);
        return AddRunning(handle.ScriptPath, acceptsInput: true);
    }

    public void Dismiss(ScriptProcess handle)
    {
        DismissCalls.Add(handle);
        _active.Remove(handle);
    }

    public void ShutdownAll(bool kill) { }

    /// <summary>The scripts whose recorded runs <see cref="Recapture"/> reports gone instead of re-attaching.</summary>
    public HashSet<string> GoneScripts { get; } = new();

    /// <summary>Re-attaches each recorded run as a Running process, mirroring the real runner so
    /// the view model's recapture→Recent wiring is exercisable, except the runs of <see cref="GoneScripts"/>,
    /// which it returns. A recaptured run owns no stdin pipe, so AcceptsInput stays false; it has no live
    /// OS Process, so Pid/OsStartedAt read null.</summary>
    public IReadOnlyList<RunRecord> Recapture(IReadOnlyList<RunRecord> runs)
    {
        if (RecaptureException is not null)
            throw RecaptureException;
        RecaptureCalls.AddRange(runs);
        var gone = new List<RunRecord>();
        foreach (var run in runs)
        {
            if (GoneScripts.Contains(run.ScriptPath))
                gone.Add(run);
            else
                Add(new ScriptProcess(_nextId++, run.ScriptPath, run.StartedAt) { LogFilePath = run.OutputPath, Recaptured = run });
        }
        return gone;
    }

    public void ReconcileExited() { }

    // As the real runner does, a handle's end raises RunEnded.
    private void Add(ScriptProcess process)
    {
        process.StateChanged += (_, _) => RunEnded?.Invoke(this, process);
        _active.Add(process);
    }

    public int ImportCalls { get; private set; }

    public Task ImportFinishedOutputAsync(IRecordStore records, CancellationToken cancellationToken)
    {
        ImportCalls++;
        return Task.CompletedTask;
    }
}
