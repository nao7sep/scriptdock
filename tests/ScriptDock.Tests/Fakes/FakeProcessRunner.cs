using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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

    /// <summary>Arrange a running process for a path directly (a ScriptProcess is Running on creation).
    /// <paramref name="acceptsInput"/> mirrors the real runner: a run this session started owns a
    /// stdin pipe (true); a recaptured run does not (false, the default for this arrange helper).</summary>
    public ScriptProcess AddRunning(string scriptPath, bool acceptsInput = false)
    {
        var process = new ScriptProcess(_nextId++, scriptPath, DateTimeOffset.UtcNow) { AcceptsInput = acceptsInput };
        _active.Add(process);
        return process;
    }

    public ScriptProcess Start(string scriptPath)
    {
        StartCalls.Add(scriptPath);
        return AddRunning(scriptPath, acceptsInput: true); // the real Start owns the run's stdin pipe
    }

    public void Terminate(ScriptProcess handle) => TerminateCalls.Add(handle);

    public Task<ScriptProcess> RestartAsync(ScriptProcess handle)
    {
        RestartCalls.Add(handle);
        _active.Remove(handle);
        return Task.FromResult(AddRunning(handle.ScriptPath, acceptsInput: true));
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
