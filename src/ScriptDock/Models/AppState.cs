using System.Collections.Generic;
using System.Linq;
using ScriptDock.Storage;

namespace ScriptDock.Models;

/// <summary>
/// Volatile session state, persisted to <c>~/.scriptdock/state.json</c>. Regenerable UI
/// state that should not churn the durable preferences in <see cref="AppConfig"/>.
/// </summary>
/// <remarks>
/// Phase 1 adds the recently-run list; <see cref="KnownPaths"/> is used by the Phase 2
/// scanner to compute the new/removed diff.
/// </remarks>
public sealed class AppState : IJsonNormalizable
{
    /// <summary>Whether hidden scripts are currently shown.</summary>
    public bool ShowHidden { get; set; }

    /// <summary>The set of script paths seen at the last acknowledged scan, against which
    /// the next scan computes its new/removed diff.</summary>
    public List<string> KnownPaths { get; set; } = [];

    /// <summary>Recently-run scripts, held newest-first (sorted on <see cref="RecentRun.RanAt"/>).</summary>
    public List<RecentRun> RecentlyRun { get; set; } = [];

    /// <summary>Persisted width of the Recent pane (the resizable right column); null until first saved.</summary>
    public double? RecentPaneWidth { get; set; }

    /// <summary>Persisted height of the Console pane; null until first saved.</summary>
    public double? ConsoleHeight { get; set; }

    /// <summary>Snapshot of the scripts that were running when this state was last saved, recorded
    /// so a relaunch can recapture them by PID + start-time. Replaced whenever the running set changes.</summary>
    public List<PersistedProcess> RunningProcesses { get; set; } = [];

    public void NormalizeAfterLoad()
    {
        KnownPaths = KnownPaths?.OfType<string>().ToList() ?? [];
        RecentlyRun = RecentlyRun?.OfType<RecentRun>()
            .Where(run => !string.IsNullOrEmpty(run.Path)).ToList() ?? [];
        RunningProcesses = RunningProcesses?.OfType<PersistedProcess>()
            .Where(process => process.Pid > 0 && !string.IsNullOrEmpty(process.ScriptPath))
            .ToList() ?? [];
        foreach (var process in RunningProcesses)
            process.LogFilePath ??= string.Empty;
    }
}
