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
    public bool TerminateResult { get; set; } = true;
    public bool RestartResult { get; set; } = true;

    /// <summary>When set, <see cref="RestartAsync"/> awaits it before completing — lets a test hold a
    /// restart "in flight" long enough to exercise a re-entrancy guard, the way the real 10-second
    /// termination grace does.</summary>
    public TaskCompletionSource? RestartGate { get; set; }

    public event EventHandler? ProcessesChanged { add { } remove { } }

    public event EventHandler<ScriptProcess>? RunEnded;

    public IReadOnlyList<ScriptProcess> Active => _active;

    /// <summary>Arrange a running process for a path directly (a ScriptProcess is Running on creation).</summary>
    public ScriptProcess AddRunning(string scriptPath)
    {
        var process = new ScriptProcess(_nextId++, scriptPath, DateTimeOffset.UtcNow);
        Add(process);
        return process;
    }

    public ScriptProcess Start(string scriptPath)
    {
        StartCalls.Add(scriptPath);
        return AddRunning(scriptPath);
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
        return AddRunning(handle.ScriptPath);
    }

    public void Dismiss(ScriptProcess handle)
    {
        DismissCalls.Add(handle);
        _active.Remove(handle);
    }

    public int StopAllCalls { get; private set; }

    public Task StopAllAsync()
    {
        StopAllCalls++;
        return Task.CompletedTask;
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
