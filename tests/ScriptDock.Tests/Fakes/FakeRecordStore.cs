using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.Storage;

namespace ScriptDock.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IRecordStore"/>. <see cref="ThrowOnWrite"/> faults the awaited writes the way the
/// real store does when its database cannot take them.
/// </summary>
public sealed class FakeRecordStore : IRecordStore
{
    public string Session { get; set; } = "2026-01-01T00:00:00.000Z";
    public bool ThrowOnWrite { get; set; }
    public bool DatabaseUnavailable { get; set; }
    public List<RunRecord> Runs { get; } = [];
    public List<RunEnd> RunEnds { get; } = [];
    public List<string> Dismissals { get; } = [];
    private readonly List<(string Path, DateTimeOffset At)> _dismissedAt = [];
    public List<ScanReport> ScanReports { get; } = [];
    public Dictionary<(string Session, int Run), byte[]> Outputs { get; } = [];

    public Task AddRunAsync(RunRecord run) => Write(() => Runs.Add(run));

    public Task AddRunEndAsync(RunEnd end) => Write(() => RunEnds.Add(end));

    public Task AddDismissalAsync(string scriptPath) => Write(() =>
    {
        Dismissals.Add(scriptPath);
        _dismissedAt.Add((scriptPath, DateTimeOffset.UtcNow));
    });

    public Task<IReadOnlyList<RecentRun>> ReadRecentAsync() =>
        Task.FromResult<IReadOnlyList<RecentRun>>(RecentRuns.From(
            Runs.Select(run => new RecentRun
            {
                Path = run.ScriptPath,
                RanAt = run.StartedAt,
                End = RunEnds
                    .Where(end => end.RunSession == run.Session && end.Run == run.Run)
                    .Select(end => new RecordedEnd(end.State, end.ExitCode))
                    .FirstOrDefault(),
            }),
            _dismissedAt));

    public void AddScanReport(ScanReport report) => ScanReports.Add(report);

    public Task<IReadOnlyDictionary<string, RunRecord>> FindRunsByOutputPathAsync(IReadOnlyCollection<string> outputPaths) =>
        Task.FromResult<IReadOnlyDictionary<string, RunRecord>>(Runs
            .Where(run => run.OutputPath is not null && outputPaths.Contains(run.OutputPath))
            .ToDictionary(run => run.OutputPath!));

    public Task AddRunOutputAsync(RunRecord run, byte[] output) => Write(() => Outputs[(run.Session, run.Run)] = output);

    private Task Write(Action write)
    {
        if (ThrowOnWrite)
            return Task.FromException(new IOException("record failed (test)"));
        write();
        return Task.CompletedTask;
    }
}
