using System;
using System.Collections.Generic;
using ScriptDock.Models;
using ScriptDock.Services;

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

    public event EventHandler? ProcessesChanged { add { } remove { } }

    public IReadOnlyList<ScriptProcess> Active => _active;

    /// <summary>Arrange a running process for a path directly (a ScriptProcess is Running on creation).</summary>
    public ScriptProcess AddRunning(string scriptPath)
    {
        var process = new ScriptProcess(_nextId++, scriptPath, DateTimeOffset.UtcNow);
        _active.Add(process);
        return process;
    }

    public ScriptProcess Start(string scriptPath)
    {
        StartCalls.Add(scriptPath);
        return AddRunning(scriptPath);
    }

    public void Terminate(ScriptProcess handle) => TerminateCalls.Add(handle);

    public ScriptProcess Restart(ScriptProcess handle, TimeSpan? grace = null)
    {
        RestartCalls.Add(handle);
        _active.Remove(handle);
        return AddRunning(handle.ScriptPath);
    }

    public void Dismiss(ScriptProcess handle)
    {
        DismissCalls.Add(handle);
        _active.Remove(handle);
    }

    public void ShutdownAll(bool kill) { }

    public void Recapture(IReadOnlyList<PersistedProcess> records) { }

    public void ReconcileExited() { }
}
