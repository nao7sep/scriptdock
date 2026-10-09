using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ScriptDock.Models;
using ScriptDock.Services;

namespace ScriptDock.Storage;

/// <summary>
/// <c>records.sqlite3</c> under the storage root: log lines, runs, run ends, run output, dismissals from the
/// Recent list and scan reports, each carrying
/// the session it came from, per the data-lifecycle-conventions' Records section and the logging-conventions.
/// One thread owns the connection and runs every write and read in the order it was queued, so no caller
/// waits on the disk. A write that fails is appended to this session's plain text file under <c>logs/</c>,
/// then to the console. The Records window's reads are <see cref="RecordQueries"/>.
/// </summary>
public sealed class RecordStore : IRecordStore, IRecordReader, IDisposable
{
    public const string FileName = "records.sqlite3";

    private static readonly TimeSpan FlushWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(5);

    // Every table carries the session (one process launch, by its start time) and the time of its record.
    // A run is named by its session and in-session run id; its output and the log lines about it carry the
    // same pair, and its end names it by that pair while carrying the session that saw it end.
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS logs (
          id      INTEGER PRIMARY KEY,
          session TEXT NOT NULL,
          time    TEXT NOT NULL,
          level   TEXT NOT NULL,
          message TEXT NOT NULL,
          line    TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_logs_session ON logs (session);
        CREATE TABLE IF NOT EXISTS runs (
          id            INTEGER PRIMARY KEY,
          session       TEXT NOT NULL,
          run           INTEGER NOT NULL,
          time          TEXT NOT NULL,
          script        TEXT NOT NULL,
          pid           INTEGER,
          os_started_at TEXT,
          output_path   TEXT,
          UNIQUE (session, run)
        );
        CREATE INDEX IF NOT EXISTS idx_runs_output_path ON runs (output_path);
        CREATE TABLE IF NOT EXISTS run_ends (
          id          INTEGER PRIMARY KEY,
          session     TEXT NOT NULL,
          time        TEXT NOT NULL,
          run_session TEXT NOT NULL,
          run         INTEGER NOT NULL,
          state       TEXT NOT NULL,
          exit_code   INTEGER
        );
        CREATE INDEX IF NOT EXISTS idx_run_ends_run ON run_ends (run_session, run);
        CREATE TABLE IF NOT EXISTS run_outputs (
          id      INTEGER PRIMARY KEY,
          session TEXT NOT NULL,
          run     INTEGER NOT NULL,
          time    TEXT NOT NULL,
          output  BLOB NOT NULL,
          UNIQUE (session, run)
        );
        CREATE TABLE IF NOT EXISTS dismissals (
          id      INTEGER PRIMARY KEY,
          session TEXT NOT NULL,
          time    TEXT NOT NULL,
          script  TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS scan_reports (
          id      INTEGER PRIMARY KEY,
          session TEXT NOT NULL,
          time    TEXT NOT NULL,
          report  TEXT NOT NULL
        );
        """;

    // The tables Schema creates, which an unversioned database from an earlier build may hold a subset of.
    private static readonly HashSet<string> Tables = ["logs", "runs", "run_ends", "run_outputs", "dismissals", "scan_reports"];

    private static readonly JsonSerializerOptions LineOptions = new(JsonOptions.Default)
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _owner;
    private readonly SqliteConnection? _connection;
    private readonly object _fallbackGate = new();

    // The Records window's reader, started on its first read.
    private readonly object _windowGate = new();
    private BlockingCollection<Action>? _windowQueue;
    private Thread? _windowReader;
    private SqliteConnection? _windowConnection;
    private int _disposed;

    /// <summary>Opens (or creates) the database in <paramref name="directory"/> for the session that started
    /// at <paramref name="sessionStartedAt"/>. A database that cannot be opened sends every entry to the
    /// fallback file; one a newer version wrote is left untouched and refused with
    /// <see cref="NewerFormatVersionException"/>, the only exception this throws.</summary>
    public RecordStore(string directory, DateTimeOffset sessionStartedAt)
    {
        Session = TimestampConventions.IsoMillis(sessionStartedAt);
        FilePath = Path.Combine(directory, FileName);
        // Seconds suffice: one instance runs at a time, and a relaunch within the same second appends.
        FallbackPath = Path.Combine(directory, "logs", TimestampConventions.FileStamp(sessionStartedAt) + ".log");

        SqliteConnection? connection = null;
        try
        {
            Directory.CreateDirectory(directory);
            connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = FilePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
            }.ToString());
            connection.Open();
            Execute(connection, "PRAGMA busy_timeout = 1000;");
            // Before anything writes, the journal mode included.
            SqliteFormatVersion.Check(connection, FilePath, FormatVersions.Records, Tables);
            Execute(connection, "PRAGMA journal_mode = WAL;");
            Execute(connection, "PRAGMA synchronous = NORMAL;");
            Execute(connection, Schema);
            _connection = connection;
        }
        catch (NewerFormatVersionException)
        {
            connection?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            connection?.Dispose();
            Fallback(FailureLine("records: could not open the database; entries go to this file", ex), null);
        }

        _owner = new Thread(Own) { IsBackground = true, Name = "records" };
        _owner.Start();
    }

    public string Session { get; }

    public event Action? Stored;

    /// <summary>The database file.</summary>
    public string FilePath { get; }

    /// <summary>This session's plain text file for entries the database could not take.</summary>
    public string FallbackPath { get; }

    /// <summary>True when the database could not be opened this session (other than a newer version's,
    /// which stops startup instead): the file is left as it is, every entry goes to
    /// <see cref="FallbackPath"/>, and the app says so once.</summary>
    public bool DatabaseUnavailable => _connection is null;

    /// <summary>Queues one log line; <paramref name="line"/> is the whole event, the other values its envelope.</summary>
    internal void AddLog(string time, string level, string message, string line) =>
        Post(connection => Execute(connection,
                "INSERT INTO logs (session, time, level, message, line) VALUES ($session, $time, $level, $message, $line)",
                ("$session", Session), ("$time", time), ("$level", level), ("$message", message), ("$line", line)),
            () => line);

    public Task AddRunAsync(RunRecord run) =>
        Write(connection =>
        {
            Execute(connection,
                "INSERT INTO runs (session, run, time, script, pid, os_started_at, output_path) " +
                "VALUES ($session, $run, $time, $script, $pid, $osStartedAt, $outputPath)",
                ("$session", run.Session), ("$run", run.Run), ("$time", TimestampConventions.IsoMillis(run.StartedAt)),
                ("$script", run.ScriptPath), ("$pid", run.Pid),
                ("$osStartedAt", run.OsStartedAt is { } started ? TimestampConventions.IsoMillis(started) : null),
                ("$outputPath", run.OutputPath));
        }, () => RecordLine("run", run));

    public Task AddRunEndAsync(RunEnd end) =>
        Write(connection =>
        {
            Execute(connection,
                "INSERT INTO run_ends (session, time, run_session, run, state, exit_code) " +
                "VALUES ($session, $time, $runSession, $run, $state, $exitCode)",
                ("$session", Session), ("$time", TimestampConventions.IsoMillis(end.EndedAt)),
                ("$runSession", end.RunSession), ("$run", end.Run), ("$state", end.State), ("$exitCode", end.ExitCode));
        }, () => RecordLine("runEnd", end));

    public Task AddDismissalAsync(string scriptPath)
    {
        var time = TimestampConventions.IsoMillis(DateTimeOffset.UtcNow);
        return Write(connection =>
        {
            Execute(connection,
                "INSERT INTO dismissals (session, time, script) VALUES ($session, $time, $script)",
                ("$session", Session), ("$time", time), ("$script", scriptPath));
        }, () => RecordLine("dismissal", new { time, script = scriptPath }));
    }

    public Task<IReadOnlyList<RecentRun>> ReadRecentAsync() =>
        Read<IReadOnlyList<RecentRun>>(connection =>
            RecentRuns.From(
                LatestRuns(connection),
                LatestByScript(connection, "SELECT script, MAX(time) FROM dismissals GROUP BY script")));

    public void AddScanReport(ScanReport report)
    {
        var json = JsonSerializer.Serialize(report, LineOptions);
        Post(connection => Execute(connection,
                "INSERT INTO scan_reports (session, time, report) VALUES ($session, $time, $report)",
                ("$session", Session), ("$time", TimestampConventions.IsoMillis(report.CompletedAt)), ("$report", json)),
            () => RecordLine("scanReport", report));
    }

    public Task<IReadOnlyDictionary<string, RunRecord>> FindRunsByOutputPathAsync(IReadOnlyCollection<string> outputPaths) =>
        Read<IReadOnlyDictionary<string, RunRecord>>(connection =>
        {
            var found = new Dictionary<string, RunRecord>(PathIdentity.Comparer);
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {RunColumns} FROM runs WHERE output_path = $path";
            var path = command.Parameters.Add("$path", SqliteType.Text);
            foreach (var outputPath in outputPaths)
            {
                path.Value = outputPath;
                using var reader = command.ExecuteReader();
                if (reader.Read())
                    found[outputPath] = ReadRun(reader);
            }
            return found;
        });

    // The Records window's reads run on their own thread over a read-only connection, so a long search
    // never holds up the writes, the Recent list or the quit's flush queued behind it; WAL lets a reader
    // and the writer work side by side.
    public Task<RecordsPage> ReadRecordsPageAsync(RecordsQuery query) =>
        WindowRead(connection => RecordQueries.ReadPage(connection, query));

    public Task<RecordDetail?> ReadRecordDetailAsync(RecordKind kind, long id) =>
        WindowRead(connection => RecordQueries.ReadDetail(connection, kind, id));

    public Task<RecordSources> ReadRecordSourcesAsync() =>
        WindowRead(connection => new RecordSources(Session, RecordQueries.ReadSessions(connection)));

    public Task AddRunOutputAsync(RunRecord run, byte[] output) =>
        Write(connection =>
        {
            Execute(connection,
                "INSERT INTO run_outputs (session, run, time, output) VALUES ($session, $run, $time, $output) " +
                "ON CONFLICT (session, run) DO UPDATE SET time = excluded.time, output = excluded.output",
                ("$session", run.Session), ("$run", run.Run),
                ("$time", TimestampConventions.IsoMillis(DateTimeOffset.UtcNow)), ("$output", output));
        }, fallbackLine: null);

    /// <summary>Waits, within a bound, until everything queued so far is written.</summary>
    public void Flush()
    {
        if (Thread.CurrentThread == _owner)
            return;

        // Not disposed: if the wait times out, the owner still sets it later.
        var drained = new ManualResetEventSlim();
        if (TryQueue(drained.Set))
            drained.Wait(FlushWait);
    }

    /// <summary>Writes what is queued, within a bound, and closes the database. Later entries go to the
    /// fallback file.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _queue.CompleteAdding();
        if (_owner.Join(CloseWait))
            _connection?.Dispose();

        lock (_windowGate)
        {
            _windowQueue?.CompleteAdding();
            if (_windowReader?.Join(CloseWait) == true)
                _windowConnection?.Dispose();
        }
    }

    private Task<T> WindowRead<T>(Func<SqliteConnection, T> read)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        // A read still sees every write queued before it; the writer never waits for a read.
        var writesBefore = new ManualResetEventSlim();
        if (!TryQueue(writesBefore.Set))
            writesBefore.Set();

        void Run()
        {
            try
            {
                writesBefore.Wait();
                writesBefore.Dispose();
                _windowConnection ??= OpenWindowConnection();
                done.SetResult(read(_windowConnection));
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }

        lock (_windowGate)
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                done.SetException(new ObjectDisposedException(nameof(RecordStore)));
                return done.Task;
            }
            if (_windowQueue is null)
            {
                var queue = new BlockingCollection<Action>();
                _windowQueue = queue;
                _windowReader = new Thread(() =>
                {
                    foreach (var operation in queue.GetConsumingEnumerable())
                        operation();
                }) { IsBackground = true, Name = "records window" };
                _windowReader.Start();
            }
            _windowQueue.Add(Run);
        }
        return done.Task;
    }

    private SqliteConnection OpenWindowConnection()
    {
        if (_connection is null)
            throw new InvalidOperationException("The records database is not open.");

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        connection.Open();
        Execute(connection, "PRAGMA busy_timeout = 1000;");
        return connection;
    }

    private void Own()
    {
        foreach (var operation in _queue.GetConsumingEnumerable())
            operation();
    }

    // A write nobody awaits: its failure is kept in the fallback file with the entry it lost.
    private void Post(Action<SqliteConnection> write, Func<string> fallbackLine)
    {
        void Run()
        {
            try
            {
                write(_connection ?? throw new InvalidOperationException("The records database is not open."));
            }
            catch (Exception ex)
            {
                Fallback(fallbackLine(), _connection is null ? null : ex);
                return;
            }

            SignalStored();
        }

        if (!TryQueue(Run))
            Fallback(fallbackLine(), null);
    }

    // An awaited write: its entry goes to the fallback file when it has a line for it, and the task faults.
    private Task Write(Action<SqliteConnection> write, Func<string>? fallbackLine) =>
        Enqueue(connection =>
        {
            write(connection);
            return true;
        }, fallbackLine, stores: true);

    // A read writes nothing, so it keeps no fallback line and signals nothing.
    private Task<T> Read<T>(Func<SqliteConnection, T> read) => Enqueue(read, fallbackLine: null, stores: false);

    private void SignalStored()
    {
        try
        {
            Stored?.Invoke();
        }
        catch (Exception ex)
        {
            // A listener's failure must not stop the records' thread; it is surfaced like a failed write.
            Fallback(FailureLine("records: a stored-record listener failed", ex), null);
        }
    }

    private Task<T> Enqueue<T>(Func<SqliteConnection, T> work, Func<string>? fallbackLine, bool stores)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Run()
        {
            T result;
            try
            {
                result = work(_connection ?? throw new InvalidOperationException("The records database is not open."));
            }
            catch (Exception ex)
            {
                if (fallbackLine is not null)
                    Fallback(fallbackLine(), _connection is null ? null : ex);
                done.SetException(ex);
                return;
            }

            done.SetResult(result);
            if (stores)
                SignalStored();
        }

        if (!TryQueue(Run))
        {
            if (fallbackLine is not null)
                Fallback(fallbackLine(), null);
            done.SetException(new ObjectDisposedException(nameof(RecordStore)));
        }
        return done.Task;
    }

    private bool TryQueue(Action operation)
    {
        try
        {
            return _queue.TryAdd(operation);
        }
        catch (InvalidOperationException)
        {
            return false; // closed: the store is shutting down
        }
    }

    private void Fallback(string line, Exception? failure)
    {
        lock (_fallbackGate)
        {
            string[] lines = failure is null ? [line] : [line, FailureLine("records: write failed", failure)];
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FallbackPath)!);
                File.AppendAllLines(FallbackPath, lines);
            }
            catch (Exception ex)
            {
                try
                {
                    foreach (var text in lines)
                        Console.Error.WriteLine(text);
                    Console.Error.WriteLine($"[records] fallback file failed: {ex.GetType().Name}: {ex.Message}");
                }
                catch { /* nothing left to surface this to */ }
            }
        }
    }

    private string RecordLine(string record, object value) =>
        new JsonObject
        {
            ["time"] = TimestampConventions.IsoMillis(DateTimeOffset.UtcNow),
            ["session"] = Session,
            ["record"] = record,
            [record] = JsonSerializer.SerializeToNode(value, value.GetType(), LineOptions),
        }.ToJsonString(LineOptions);

    private string FailureLine(string message, Exception failure) =>
        new JsonObject
        {
            ["time"] = TimestampConventions.IsoMillis(DateTimeOffset.UtcNow),
            ["session"] = Session,
            ["level"] = "error",
            ["message"] = message,
            ["error"] = SessionLogger.BuildErrorNode(failure),
        }.ToJsonString(LineOptions);

    // Each script's latest run with its recorded end, if any. SQLite takes the bare session and run from the
    // row holding MAX(time), so the end joined is that latest run's.
    private static List<RecentRun> LatestRuns(SqliteConnection connection)
    {
        var latest = new List<RecentRun>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT latest.script, latest.time, run_ends.state, run_ends.exit_code
            FROM (SELECT script, MAX(time) AS time, session, run FROM runs GROUP BY script) AS latest
            LEFT JOIN run_ends ON run_ends.run_session = latest.session AND run_ends.run = latest.run
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            latest.Add(new RecentRun
            {
                Path = reader.GetString(0),
                RanAt = ParseTime(reader.GetString(1)),
                End = reader.IsDBNull(2)
                    ? null
                    : new RecordedEnd(reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt32(3)),
            });
        }
        return latest;
    }

    private static List<(string Path, DateTimeOffset At)> LatestByScript(SqliteConnection connection, string sql)
    {
        var latest = new List<(string Path, DateTimeOffset At)>();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
            latest.Add((reader.GetString(0), ParseTime(reader.GetString(1))));
        return latest;
    }

    private const string RunColumns = "session, run, time, script, pid, os_started_at, output_path";

    private static RunRecord ReadRun(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetInt32(1),
            ParseTime(reader.GetString(2)),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt32(4),
            reader.IsDBNull(5) ? null : ParseTime(reader.GetString(5)),
            reader.IsDBNull(6) ? null : reader.GetString(6));

    private static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
}
