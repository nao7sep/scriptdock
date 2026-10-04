using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ScriptDock;
using ScriptDock.Models;
using ScriptDock.Storage;
using Xunit;

namespace ScriptDock.Tests.Storage;

public sealed class RecordStoreTests : IDisposable
{
    private static readonly DateTimeOffset SessionStart = new(2026, 10, 2, 3, 4, 5, 678, TimeSpan.Zero);
    private const string Session = "2026-10-02T03:04:05.678Z";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scriptdock-records-tests", NanoId.New());

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private object? Scalar(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_dir, RecordStore.FileName)};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static RunRecord Run(int run, string outputPath) =>
        new(Session, run, SessionStart, "/x/a.command", 4242, SessionStart.AddSeconds(-1), outputPath);

    [Fact]
    public async Task Every_record_carries_its_session()
    {
        using (var records = new RecordStore(_dir, SessionStart))
        {
            records.AddLog("2026-10-02T03:04:06.000Z", "info", "hello", "{\"message\":\"hello\"}");
            await records.AddRunAsync(Run(1, "/runs/1.log"));
            records.AddScanReport(new ScanReport
            {
                CompletedAt = SessionStart,
                Roots = ["/r"],
                Found = [],
                PrunedDirectories = [],
                SkippedFiles = [],
                Inaccessible = [],
                InvalidPatterns = [],
            });
            records.Flush();
        }

        Assert.Equal(Session, Scalar("SELECT session FROM logs"));
        Assert.Equal("{\"message\":\"hello\"}", Scalar("SELECT line FROM logs"));
        Assert.Equal(Session, Scalar("SELECT session FROM runs WHERE run = 1"));
        Assert.Equal(Session, Scalar("SELECT session FROM scan_reports"));
        Assert.Contains("\"roots\":[\"/r\"]", (string)Scalar("SELECT report FROM scan_reports")!);
    }

    [Fact]
    public async Task A_run_is_found_by_its_output_path()
    {
        using var records = new RecordStore(_dir, SessionStart);
        var run = Run(7, "/runs/7.log");
        await records.AddRunAsync(run);

        var found = await records.FindRunsByOutputPathAsync(["/runs/7.log", "/runs/missing.log"]);

        Assert.Equal(run, Assert.Single(found).Value);
    }

    [Fact]
    public async Task Importing_a_run_again_replaces_its_output()
    {
        using (var records = new RecordStore(_dir, SessionStart))
        {
            var run = Run(3, "/runs/3.log");
            await records.AddRunOutputAsync(run, [1, 2]);
            await records.AddRunOutputAsync(run, [1, 2, 3]);
        }

        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM run_outputs"));
        Assert.Equal(new byte[] { 1, 2, 3 }, Scalar("SELECT output FROM run_outputs"));
    }

    [Fact]
    public async Task Recent_is_each_scripts_latest_run_that_no_later_dismissal_took_off()
    {
        using var records = new RecordStore(_dir, SessionStart);
        var past = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await records.AddRunAsync(Run(1, "/runs/1.log") with { ScriptPath = "/x/a.command", StartedAt = past });
        await records.AddRunAsync(Run(2, "/runs/2.log") with { ScriptPath = "/x/b.command", StartedAt = past.AddMinutes(1) });
        await records.AddRunAsync(Run(3, "/runs/3.log") with { ScriptPath = "/x/a.command", StartedAt = past.AddMinutes(2) });
        await records.AddDismissalAsync("/x/b.command");

        var recent = await records.ReadRecentAsync();

        var only = Assert.Single(recent);
        Assert.Equal("/x/a.command", only.Path);
        Assert.Equal(past.AddMinutes(2), only.RanAt);
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM dismissals"));
    }

    [Fact]
    public async Task A_runs_end_is_recorded_with_its_state_and_exit_code()
    {
        using var records = new RecordStore(_dir, SessionStart);
        await records.AddRunAsync(Run(2, "/runs/2.log"));
        await records.AddRunEndAsync(new RunEnd(Session, 2, SessionStart.AddMinutes(1), "exited", 0));

        Assert.Equal(Session, Scalar("SELECT session FROM run_ends"));
        Assert.Equal("exited", Scalar("SELECT state FROM run_ends"));
        Assert.Equal(0L, Scalar("SELECT exit_code FROM run_ends"));
    }

    [Fact]
    public async Task Without_a_database_entries_go_to_the_sessions_fallback_file()
    {
        Directory.CreateDirectory(Path.Combine(_dir, RecordStore.FileName)); // a directory cannot be opened as one
        using var records = new RecordStore(_dir, SessionStart);

        records.AddLog("2026-10-02T03:04:06.000Z", "warn", "kept", "{\"message\":\"kept\"}");
        await Assert.ThrowsAnyAsync<Exception>(() => records.AddRunAsync(Run(1, "/runs/1.log")));
        records.Flush();

        Assert.Equal(Path.Combine(_dir, "logs", "20261002-030405-678-utc.log"), records.FallbackPath);
        var text = File.ReadAllText(records.FallbackPath);
        Assert.Contains("could not open the database", text);
        Assert.Contains("{\"message\":\"kept\"}", text);
        Assert.Contains("\"record\":\"run\"", text);
    }

    [Fact]
    public void After_closing_entries_go_to_the_fallback_file()
    {
        var records = new RecordStore(_dir, SessionStart);
        records.Dispose();

        records.AddLog("2026-10-02T03:04:06.000Z", "info", "late", "{\"message\":\"late\"}");

        Assert.Contains("{\"message\":\"late\"}", File.ReadAllText(records.FallbackPath));
    }
}
