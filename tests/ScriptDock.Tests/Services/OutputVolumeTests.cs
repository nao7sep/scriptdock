using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.Storage;
using ScriptDock.Tests.Fakes;
using ScriptDock.ViewModels;
using Xunit;

namespace ScriptDock.Tests.Services;

/// <summary>
/// Output of any size costs memory and interface time by what is new or shown, not by its whole size
/// (volume-handling-conventions): it is cleaned and stored in pieces, a log too large for one record stays
/// as its file, and the console reads only what a log gained.
/// </summary>
public sealed class OutputVolumeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scriptdock-volume-" + NanoId.New());

    public OutputVolumeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    // The cleanup as it was written over a whole string, the reference the streamed one must match.
    private static List<string> Reference(string output) =>
        output.Replace("\r\n", "\n").Split('\n')
            .Select(line => line.LastIndexOf('\r') is var index and >= 0 ? line[(index + 1)..] : line)
            .Select(AnsiStripper.Strip)
            .ToList();

    public static TheoryData<string> Outputs => new()
    {
        "",
        "one",
        "one\n",
        "one\ntwo\n\nthree",
        "a\r\nb\r\n",
        "progress 1\rprogress 2\rdone\n",
        "a\r\r\nb",
        "ends with a return\r",
        "\u001b[31mred\u001b[0m and \u001b]0;title\u0007plain\n",
        "日本語\r\n✓ 完了\n",
    };

    [Theory]
    [MemberData(nameof(Outputs))]
    public void Lines_match_the_whole_string_cleanup(string output) =>
        Assert.Equal(Reference(output), RunLog.Lines(output));

    [Theory]
    [MemberData(nameof(Outputs))]
    public void Copy_streams_the_same_text_whatever_the_pieces(string output)
    {
        var expected = string.Join("\n", Reference(output));
        var writer = new StringWriter();
        RunLog.Copy(new MemoryStream(Encoding.UTF8.GetBytes(output)), writer);
        Assert.Equal(expected, writer.ToString());

        // Fed one character at a time, so every boundary falls inside a CRLF, an escape or a character.
        var pieces = new StringBuilder();
        var cleaner = new RunLog.OutputCleaner(text => pieces.Append(text), () => pieces.Append('\n'));
        foreach (var c in output)
            cleaner.Write([c]);
        cleaner.Complete();
        Assert.Equal(expected, pieces.ToString());
    }

    // A line far longer than the working buffer is passed on in pieces, its escapes still removed whole.
    [Fact]
    public void A_line_longer_than_the_working_buffer_is_passed_on_in_pieces_with_its_escapes_removed()
    {
        var giant = new string('x', RunLog.OutputCleaner.HeldChars - 3) + "\u001b[31m" + new string('y', RunLog.OutputCleaner.HeldChars) + "\u001b[0m";
        var writer = new StringWriter();

        RunLog.Copy(new MemoryStream(Encoding.UTF8.GetBytes(giant + "\nend")), writer);

        Assert.Equal(new string('x', RunLog.OutputCleaner.HeldChars - 3) + new string('y', RunLog.OutputCleaner.HeldChars) + "\nend", writer.ToString());
    }

    // ── The console feed ────────────────────────────────────────────────────────────────────────

    private string Log(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static void Append(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        stream.Write(bytes);
    }

    [Fact]
    public void An_unfinished_line_arriving_in_small_pieces_stays_bounded()
    {
        var path = Log("growing.log", "first\n");
        var feed = new ConsoleFeed(path);
        feed.Read();
        ConsoleUpdate update = null!;
        for (var index = 0; index < 40; index++)
        {
            Append(path, Encoding.UTF8.GetBytes(new string('x', 64 * 1024)));
            update = feed.Read()!;
            Assert.False(update.Reset);
            Assert.True(update.Open.Length <= ConsoleFeed.ShownBytes);
        }
        Append(path, Encoding.UTF8.GetBytes("newer\nlast\n"));
        update = feed.Read()!;
        Assert.Equal(ConsoleFeed.ShownBytes, update.Completed[0].Length);
        Assert.Equal("last", update.Completed[1]);
    }

    [Fact]
    public void The_console_reads_only_what_the_log_gained_and_redraws_the_line_still_arriving()
    {
        var path = Log("run.log", "one\ntwo\npartial");
        var feed = new ConsoleFeed(path);

        var first = feed.Read()!;
        Assert.True(first.Reset);
        Assert.Equal(["one", "two"], first.Completed);
        Assert.Equal("partial", first.Open);
        Assert.Null(feed.Read()); // nothing new

        Append(path, Encoding.UTF8.GetBytes(" done\nprogress 1\rprogress 2"));
        var next = feed.Read()!;
        Assert.False(next.Reset);
        Assert.Equal(["partial done"], next.Completed);
        Assert.Equal("progress 2", next.Open);
    }

    [Fact]
    public void A_character_split_between_reads_arrives_whole()
    {
        var bytes = Encoding.UTF8.GetBytes("日本\n");
        var path = Log("split.log", "");
        File.WriteAllBytes(path, bytes[..2]);
        var feed = new ConsoleFeed(path);
        Assert.Equal("", feed.Read()!.Open);

        Append(path, bytes[2..]);
        Assert.Equal(["日本"], feed.Read()!.Completed);
    }

    [Fact]
    public void The_console_shows_the_latest_part_of_a_long_log_and_skips_a_burst_it_could_not_show()
    {
        var line = new string('z', 99) + "\n";
        var path = Log("long.log", string.Concat(Enumerable.Repeat(line, ConsoleFeed.ShownBytes / 100 * 3)));
        var feed = new ConsoleFeed(path);

        var first = feed.Read()!;
        Assert.True(first.Reset);
        Assert.InRange(first.Completed.Count, ConsoleFeed.ShownBytes / 100 - 2, ConsoleFeed.ShownBytes / 100);
        Assert.All(first.Completed, text => Assert.Equal(99, text.Length)); // the cut first line is dropped

        Append(path, Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(line, ConsoleFeed.ShownBytes / 100 * 2))));
        Assert.True(feed.Read()!.Reset); // more than it shows: it starts over at the end, not with all of it
    }

    [Fact]
    public void The_console_appends_lines_replaces_the_one_still_arriving_and_lets_the_oldest_go()
    {
        var vm = new MainWindowViewModel(
            new FakeConfigStore(), new FakeJsonStore<AppState>(), new FakeJsonStore<KnownPaths>(), new FakeRecordStore(),
            new AppConfig(), new AppState(), new KnownPaths(), new ScriptScanner(), new FakeProcessRunner());

        vm.ApplyConsoleUpdate(new ConsoleUpdate(true, ["a", "b"], "c-partial"));
        var b = vm.ConsoleLines[1];
        vm.ApplyConsoleUpdate(new ConsoleUpdate(false, ["c-done"], "d"));
        Assert.Equal(["a", "b", "c-done", "d"], vm.ConsoleLines.Select(line => line.Text));
        Assert.Same(b, vm.ConsoleLines[1]); // lines already shown stay as they are

        var big = new string('q', ConsoleFeed.ShownBytes / 2);
        vm.ApplyConsoleUpdate(new ConsoleUpdate(false, [big, big, big], ""));
        Assert.DoesNotContain(vm.ConsoleLines, line => line.Text == "a");
        Assert.Equal(big, vm.ConsoleLines[^1].Text);
    }

    [Fact]
    public void A_very_long_console_line_is_drawn_shortened_and_kept_whole_for_copying()
    {
        var text = new string('w', RecordFormat.OutputShownLineGraphemes * 3);
        var line = new ConsoleLine(text);

        Assert.Equal(text, line.Text);
        Assert.EndsWith("…", line.Shown);
        Assert.True(line.Shown.Length <= RecordFormat.OutputShownLineGraphemes + 1);
        Assert.Same("short", new ConsoleLine("short").Shown);
    }

    // ── Import into the records ─────────────────────────────────────────────────────────────────

    private static RunRecord Run(int run, string output) =>
        new("2026-01-01T00:00:00.000Z", run, DateTimeOffset.UtcNow, "/x/a.command", null, null, output);

    private string QuietLog(string name, int bytes)
    {
        var path = Path.Combine(_dir, "runs", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Enumerable.Repeat((byte)'o', bytes).ToArray());
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - ProcessRunner.OutputQuietPeriod - TimeSpan.FromMinutes(1));
        return path;
    }

    [Fact]
    public async Task A_log_too_large_for_one_record_stays_as_its_file_and_the_others_are_imported()
    {
        var small = QuietLog("small.log", 10);
        var large = QuietLog("large.log", 64);
        var records = new FakeRecordStore { MaxOutputBytes = 32 };
        records.Runs.Add(Run(1, small));
        records.Runs.Add(Run(2, large));
        var runner = new ProcessRunner(Path.Combine(_dir, "runs"));

        await runner.ImportFinishedOutputAsync(records, TestContext.Current.CancellationToken);
        await runner.ImportFinishedOutputAsync(records, TestContext.Current.CancellationToken); // a later pass changes nothing

        Assert.False(File.Exists(small));
        Assert.Equal(10, records.Outputs[("2026-01-01T00:00:00.000Z", 1)].Length);
        Assert.True(File.Exists(large));
        Assert.DoesNotContain(("2026-01-01T00:00:00.000Z", 2), records.Outputs.Keys);
    }

    [MacOnlyFact]
    [SupportedOSPlatform("macos")]
    public async Task A_permission_denied_log_does_not_hold_up_another_log_and_is_retried()
    {
        var unreadable = QuietLog("a-unreadable.log", 10);
        var fine = QuietLog("b-fine.log", 10);
        var records = new FakeRecordStore();
        records.Runs.Add(Run(1, unreadable));
        records.Runs.Add(Run(2, fine));
        var runner = new ProcessRunner(Path.Combine(_dir, "runs"));
        var mode = File.GetUnixFileMode(unreadable);
        File.SetUnixFileMode(unreadable, UnixFileMode.None);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => File.OpenRead(unreadable));
            await runner.ImportFinishedOutputAsync(records, TestContext.Current.CancellationToken);
            AssertIndependentImportFailure(records, unreadable, fine);
        }
        finally
        {
            File.SetUnixFileMode(unreadable, mode);
        }

        await runner.ImportFinishedOutputAsync(records, TestContext.Current.CancellationToken);
        Assert.Equal(2, records.Outputs.Count);
        Assert.False(File.Exists(unreadable));
    }

    [WindowsOnlyFact]
    public async Task A_sharing_denied_log_does_not_hold_up_another_log_and_is_retried()
    {
        var unreadable = QuietLog("a-unreadable.log", 10);
        var fine = QuietLog("b-fine.log", 10);
        var records = new FakeRecordStore();
        records.Runs.Add(Run(1, unreadable));
        records.Runs.Add(Run(2, fine));
        var runner = new ProcessRunner(Path.Combine(_dir, "runs"));
        using (var held = new FileStream(unreadable, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(() => File.OpenRead(unreadable));
            await runner.ImportFinishedOutputAsync(records, TestContext.Current.CancellationToken);
            AssertIndependentImportFailure(records, unreadable, fine);
        }

        await runner.ImportFinishedOutputAsync(records, TestContext.Current.CancellationToken);
        Assert.Equal(2, records.Outputs.Count);
        Assert.False(File.Exists(unreadable));
    }

    private static void AssertIndependentImportFailure(FakeRecordStore records, string unreadable, string fine)
    {
        Assert.True(File.Exists(unreadable));
        Assert.False(File.Exists(fine));
        Assert.DoesNotContain(("2026-01-01T00:00:00.000Z", 1), records.Outputs.Keys);
        Assert.Contains(("2026-01-01T00:00:00.000Z", 2), records.Outputs.Keys);
    }

    // Output a process the run left behind writes while the copy runs is not lost: the changed file
    // is not deleted, and its next quiet pass imports it whole.
    [Fact]
    public async Task A_source_that_changes_during_the_copy_stays_and_is_imported_whole_later()
    {
        var path = QuietLog("changing.log", 10);
        var records = new FakeRecordStore();
        records.Runs.Add(Run(1, path));
        records.BeforeOutputCopy = _ =>
        {
            records.BeforeOutputCopy = null;
            Append(path, "late"u8.ToArray());
        };
        var runner = new ProcessRunner(Path.Combine(_dir, "runs"));

        await runner.ImportFinishedOutputAsync(records, TestContext.Current.CancellationToken);

        Assert.Equal(10, Assert.Single(records.Outputs).Value.Length); // only the captured extent
        Assert.True(File.Exists(path));
        Assert.Equal(14, new FileInfo(path).Length);

        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - ProcessRunner.OutputQuietPeriod - TimeSpan.FromMinutes(1));
        var whole = File.ReadAllBytes(path);
        await runner.ImportFinishedOutputAsync(records, TestContext.Current.CancellationToken);

        Assert.Equal(whole, Assert.Single(records.Outputs).Value);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task A_failed_blob_write_keeps_the_original_for_a_later_pass()
    {
        var path = QuietLog("failed.log", 10);
        var original = File.ReadAllBytes(path);
        var records = new FakeRecordStore { ThrowOnWrite = true };
        records.Runs.Add(Run(1, path));
        var runner = new ProcessRunner(Path.Combine(_dir, "runs"));

        await runner.ImportFinishedOutputAsync(records, TestContext.Current.CancellationToken);

        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(records.Outputs);

        records.ThrowOnWrite = false;
        await runner.ImportFinishedOutputAsync(records, TestContext.Current.CancellationToken);
        Assert.Equal(original, Assert.Single(records.Outputs).Value);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Output_is_stored_in_pieces_and_a_copy_that_fails_part_way_leaves_the_earlier_record()
    {
        var root = Path.Combine(_dir, "records");
        using var records = new RecordStore(root, DateTimeOffset.UtcNow);
        var run = new RunRecord(records.Session, 1, DateTimeOffset.UtcNow, "/x/a.command", null, null, null);
        await records.AddRunAsync(run);
        var big = Enumerable.Range(0, RecordStore.CopyBufferBytes * 3 + 7).Select(i => (byte)('a' + i % 26)).ToArray();
        await records.AddRunOutputAsync(run, big);
        var id = (await records.ReadRecordsPageAsync(new RecordsQuery(null, RecordKind.Run, null, "", null), cancellationToken: TestContext.Current.CancellationToken)).Records.Single(summary => summary.Kind == RecordKind.Run).Id;
        Assert.Equal(big, await records.ReadRunOutputAsync(id));

        await Assert.ThrowsAnyAsync<Exception>(() => records.AddRunOutputAsync(run, new MemoryStream(new byte[10]), 20));
        Assert.Equal(big, await records.ReadRunOutputAsync(id)); // the failed replacement left no part behind
    }

    // A run whose output is too large for one record is read from its own file: its preview and its whole
    // output, marked so the detail can say search does not cover it.
    [Fact]
    public async Task A_kept_output_file_is_previewed_and_read_from_the_file()
    {
        var root = Path.Combine(_dir, "kept");
        long id;
        string session;
        var file = Path.Combine(_dir, "kept.log");
        File.WriteAllText(file, new string('k', 3000));
        using (var records = new RecordStore(root, DateTimeOffset.UtcNow))
        {
            session = records.Session;
            await records.AddRunAsync(new RunRecord(session, 1, DateTimeOffset.UtcNow, "/x/a.command", null, null, file));
            id = (await records.ReadRecordsPageAsync(new RecordsQuery(null, RecordKind.Run, null, "", null), cancellationToken: TestContext.Current.CancellationToken)).Records.Single().Id;
        }

        using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "records.sqlite3")}");
        connection.Open();
        SQLitePCL.raw.sqlite3_limit(connection.Handle, SQLitePCL.raw.SQLITE_LIMIT_LENGTH, 1000); // a stand-in for 1 GB

        var detail = Assert.IsType<RunRecordDetail>(RecordQueries.ReadDetail(connection, RecordKind.Run, id));
        Assert.True(detail.OutputKeptAsFile);
        Assert.Equal(3000, detail.OutputLength);
        Assert.Equal(3000, detail.Output!.Length);

        byte[]? whole = null;
        Assert.True(RecordQueries.ReadRunOutput(connection, id, stream =>
        {
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            whole = copy.ToArray();
        }));
        Assert.Equal(3000, whole!.Length);
    }

    // A run output file that exists but cannot be read is a failed read, not a run without output.
    [MacOnlyFact]
    [SupportedOSPlatform("macos")]
    public async Task A_denied_output_file_fails_the_read_instead_of_showing_no_output()
    {
        var root = Path.Combine(_dir, "denied");
        var file = Path.Combine(_dir, "denied.log");
        File.WriteAllText(file, "output");
        using var records = new RecordStore(root, DateTimeOffset.UtcNow);
        await records.AddRunAsync(new RunRecord(records.Session, 1, DateTimeOffset.UtcNow, "/x/a.command", null, null, file));
        var id = (await records.ReadRecordsPageAsync(new RecordsQuery(null, RecordKind.Run, null, "", null), cancellationToken: TestContext.Current.CancellationToken)).Records.Single().Id;
        var mode = File.GetUnixFileMode(file);
        try
        {
            File.SetUnixFileMode(file, UnixFileMode.UserWrite);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => records.ReadRecordDetailAsync(RecordKind.Run, id, cancellationToken: TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => records.ReadRunOutputAsync(id, Stream.Null));
        }
        finally { File.SetUnixFileMode(file, mode); }
    }
}
