using System;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ScriptDock.Models;
using ScriptDock.Services;

namespace ScriptDock.ViewModels;

/// <summary>
/// Bindable wrapper over a running or finished <see cref="ScriptProcess"/>. Forwards the
/// process's state changes to the UI as property notifications, marshalled to the UI
/// thread (the process raises them from a background thread). Dispose to unsubscribe when
/// the row is removed.
/// </summary>
public sealed class ProcessItem : ObservableObject, IDisposable
{
    public ProcessItem(ScriptProcess process)
    {
        Process = process;
        Process.StateChanged += OnStateChanged;
    }

    public ScriptProcess Process { get; }

    public int Id => Process.Id;
    public string ScriptPath => Process.ScriptPath;
    public string DisplayName => ScriptDisplayName.For(Process.ScriptPath);
    public DateTimeOffset StartedAt => Process.StartedAt;
    public RunState State => Process.State;
    public int? ExitCode => Process.ExitCode;

    /// <summary>A compact status for the row: <c>running</c>, <c>exited (0)</c>, etc.</summary>
    public string StateText => State switch
    {
        RunState.Running => "running",
        RunState.Exited => $"exited ({ExitCode})",
        RunState.Terminated => "terminated",
        RunState.Failed => "failed",
        _ => State.ToString().ToLowerInvariant(),
    };

    private void OnStateChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(ExitCode));
            OnPropertyChanged(nameof(StateText));
        });
    }

    public void Dispose() => Process.StateChanged -= OnStateChanged;
}
