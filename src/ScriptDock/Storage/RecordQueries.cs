using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using ScriptDock.Models;

namespace ScriptDock.Storage;

/// <summary>
/// The Records window's reads over <c>records.sqlite3</c>: a filtered page of summaries, newest first,
/// keyset-paged by the last summary shown, one record whole, and the launches that have records. Each runs
/// on the records' own thread (<see cref="RecordStore"/>), so a read sees every entry queued before it.
/// </summary>
internal static class RecordQueries
{
    public const int PageSize = 100;

    // A run's output reads as an error when the run's latest recorded end is a failure or an exit with
    // another code than 0; with no recorded end it reads as info.
    private const string RunOutputLevel = """
        COALESCE((SELECT CASE WHEN e.state = 'failed' OR (e.state = 'exited' AND COALESCE(e.exit_code, 0) <> 0)
                              THEN 'error' ELSE 'info' END
                  FROM run_ends e WHERE e.run_session = o.session AND e.run = o.run
                  ORDER BY e.time DESC, e.id DESC LIMIT 1), 'info')
        """;

    private const string ScanFound =
        "CASE WHEN json_valid(report) THEN json_array_length(report, '$.found') END";

    private const string ScanRoots =
        "CASE WHEN json_valid(report) THEN (SELECT group_concat(value, ', ') FROM json_each(report, '$.roots')) END";

    public static RecordsPage ReadPage(SqliteConnection connection, RecordsQuery query)
    {
        using var command = connection.CreateCommand();
        var parts = new List<string>();
        var search = LikePattern(query.Search);
        if (query.Session is not null)
            command.Parameters.AddWithValue("$session", query.Session);
        if (search is not null)
            command.Parameters.AddWithValue("$search", search);
        if (query.Level is { } level and not RecordLevelFilter.Attention)
            command.Parameters.AddWithValue("$level", LevelName(level));

        string Where(string session, string levelColumn, params string[] searched)
        {
            var where = new List<string> { "1 = 1" };
            if (query.Session is not null)
                where.Add($"{session} = $session");
            if (query.Level == RecordLevelFilter.Attention)
                where.Add($"{levelColumn} IN ('warn', 'error')");
            else if (query.Level is not null)
                where.Add($"{levelColumn} = $level");
            if (search is not null)
                where.Add("(" + string.Join(" OR ", System.Array.ConvertAll(searched, column => $"{column} LIKE $search ESCAPE '\\'")) + ")");
            return string.Join(" AND ", where);
        }

        if (query.Kind is null or RecordKind.Log)
        {
            parts.Add(
                "SELECT 'log' AS kind, id, session, time, level, message AS title, NULL AS text, NULL AS found " +
                $"FROM logs WHERE {Where("session", "level", "message", "line")}");
        }

        if (query.Kind is null or RecordKind.RunOutput)
        {
            parts.Add(
                $"SELECT 'run-output' AS kind, o.id, o.session, o.time, {RunOutputLevel} AS level, " +
                "COALESCE(r.script, '') AS title, NULL AS text, NULL AS found " +
                "FROM run_outputs o LEFT JOIN runs r ON r.session = o.session AND r.run = o.run " +
                $"WHERE {Where("o.session", RunOutputLevel, "r.script", "CAST(o.output AS TEXT)")}");
        }

        // A scan report has no level of its own and reads as info.
        if ((query.Kind is null or RecordKind.ScanReport)
            && (query.Level is null or RecordLevelFilter.Info))
        {
            parts.Add(
                $"SELECT 'scan-report' AS kind, id, session, time, 'info' AS level, '' AS title, {ScanRoots} AS text, " +
                $"{ScanFound} AS found FROM scan_reports WHERE {Where("session", "'info'", "report")}");
        }

        if (parts.Count == 0)
            return new RecordsPage([], false);

        var after = "";
        if (query.After is { } cursor)
        {
            after = "WHERE time < $afterTime OR (time = $afterTime AND (kind < $afterKind OR (kind = $afterKind AND id < $afterId)))";
            command.Parameters.AddWithValue("$afterTime", cursor.Time);
            command.Parameters.AddWithValue("$afterKind", RecordKinds.Name(cursor.Kind));
            command.Parameters.AddWithValue("$afterId", cursor.Id);
        }

        command.Parameters.AddWithValue("$limit", PageSize + 1);
        command.CommandText =
            $"SELECT kind, id, session, time, level, title, text, found FROM ({string.Join(" UNION ALL ", parts)}) {after} " +
            "ORDER BY time DESC, kind DESC, id DESC LIMIT $limit";

        var records = new List<RecordSummary>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            records.Add(new RecordSummary(
                RecordKinds.Parse(reader.GetString(0)),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                RecordKinds.Level(reader.GetString(4)),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7)));
        }

        var more = records.Count > PageSize;
        if (more)
            records.RemoveAt(records.Count - 1);
        return new RecordsPage(records, more);
    }

    public static RecordDetail? ReadDetail(SqliteConnection connection, RecordKind kind, long id)
    {
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$id", id);
        switch (kind)
        {
            case RecordKind.Log:
            {
                command.CommandText = "SELECT id, session, time, level, message, line FROM logs WHERE id = $id";
                using var reader = command.ExecuteReader();
                return reader.Read()
                    ? new LogRecordDetail(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                        RecordKinds.Level(reader.GetString(3)), reader.GetString(4), reader.GetString(5))
                    : null;
            }

            case RecordKind.RunOutput:
            {
                command.CommandText =
                    $"SELECT o.id, o.session, o.time, {RunOutputLevel}, o.run, r.script, r.time, r.pid, r.os_started_at, " +
                    "r.output_path, e.time, e.state, e.exit_code, o.output " +
                    "FROM run_outputs o LEFT JOIN runs r ON r.session = o.session AND r.run = o.run " +
                    "LEFT JOIN run_ends e ON e.id = (SELECT id FROM run_ends WHERE run_session = o.session AND run = o.run " +
                    "ORDER BY time DESC, id DESC LIMIT 1) WHERE o.id = $id";
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                    return null;
                return new RunOutputRecordDetail(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    RecordKinds.Level(reader.GetString(3)),
                    reader.GetInt32(4),
                    TextOrNull(reader, 5),
                    TextOrNull(reader, 6),
                    reader.IsDBNull(7) ? null : reader.GetInt32(7),
                    TextOrNull(reader, 8),
                    TextOrNull(reader, 9),
                    TextOrNull(reader, 10),
                    TextOrNull(reader, 11),
                    reader.IsDBNull(12) ? null : reader.GetInt32(12),
                    (byte[])reader.GetValue(13));
            }

            case RecordKind.ScanReport:
            {
                command.CommandText = $"SELECT id, session, time, {ScanFound}, report FROM scan_reports WHERE id = $id";
                using var reader = command.ExecuteReader();
                return reader.Read()
                    ? new ScanReportRecordDetail(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetInt32(3), reader.GetString(4))
                    : null;
            }

            default:
                return null;
        }
    }

    public static IReadOnlyList<string> ReadSessions(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT session FROM logs UNION SELECT session FROM run_outputs UNION SELECT session FROM scan_reports " +
            "ORDER BY session DESC";
        var sessions = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            sessions.Add(reader.GetString(0));
        return sessions;
    }

    // Search is a plain substring: LIKE's own wildcards are taken literally.
    private static string? LikePattern(string search)
    {
        var trimmed = search.Trim();
        return trimmed.Length == 0
            ? null
            : "%" + trimmed.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
    }

    private static string LevelName(RecordLevelFilter level) => level switch
    {
        RecordLevelFilter.Error => "error",
        RecordLevelFilter.Warn => "warn",
        RecordLevelFilter.Debug => "debug",
        _ => "info",
    };

    private static string? TextOrNull(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
