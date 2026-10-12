using System.Linq;
using System.Text;
using ScriptDock.ViewModels;
using Xunit;

namespace ScriptDock.Tests.ViewModels;

/// <summary>
/// The Records detail blocks as <see cref="RecordFormat"/> makes them: a block with nothing in it is null,
/// so the pane leaves it out, and a log line's details leave out the envelope the pane already shows.
/// </summary>
public sealed class RecordFormatTests
{
    private static string? Lf(string? text) => text?.Replace("\r\n", "\n");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \n\t ")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData(" { } ")]
    [InlineData("[]")]
    [InlineData("\"\"")]
    [InlineData("\"  \"")]
    public void An_empty_value_has_no_block(string? text) => Assert.Null(RecordFormat.PrettyJson(text));

    [Fact]
    public void A_value_with_content_is_shown_indented_or_as_it_is()
    {
        Assert.Equal("{\n  \"found\": [\n    \"/a\"\n  ]\n}", Lf(RecordFormat.PrettyJson("""{"found":["/a"]}""")));
        Assert.Equal("[\n  0\n]", Lf(RecordFormat.PrettyJson("[0]")));
        Assert.Equal("0", RecordFormat.PrettyJson("0"));
        Assert.Equal("not json {", RecordFormat.PrettyJson("not json {"));
    }

    [Fact]
    public void A_log_line_s_details_leave_out_its_time_level_and_message()
    {
        var details = RecordFormat.LogDetails(
            """{"time":"2026-10-04T08:00:00.000Z","level":"error","message":"run: start failed","script":"/a.command","error":{"type":"E","message":"denied"}}""");

        Assert.Equal(
            "{\n  \"script\": \"/a.command\",\n  \"error\": {\n    \"type\": \"E\",\n    \"message\": \"denied\"\n  }\n}",
            Lf(details));
    }

    [Fact]
    public void A_log_line_with_nothing_beyond_its_envelope_has_no_details()
    {
        Assert.Null(RecordFormat.LogDetails("""{"time":"2026-10-04T08:00:00.000Z","level":"info","message":"startup"}"""));
        Assert.Null(RecordFormat.LogDetails("{}"));
        Assert.Null(RecordFormat.LogDetails(" "));
    }

    [Fact]
    public void A_log_line_that_is_not_an_object_is_shown_as_it_is()
    {
        Assert.Equal("plain text", RecordFormat.LogDetails("plain text"));
        Assert.Equal("[\n  1\n]", Lf(RecordFormat.LogDetails("[1]")));
    }

    [Fact]
    public void Output_that_is_missing_or_only_whitespace_has_no_block()
    {
        Assert.Null(RecordFormat.Output(null));
        Assert.Null(RecordFormat.Output([]));
        Assert.Null(RecordFormat.Output(Encoding.UTF8.GetBytes(" \r\n\t\n")));
        Assert.Equal(new OutputPreview("done", 0), RecordFormat.Output(Encoding.UTF8.GetBytes("done\n")));
    }

    [Fact]
    public void Output_past_the_limit_shows_its_start_and_counts_what_it_leaves_out()
    {
        var stored = Encoding.UTF8.GetBytes("first\nsecond\nthird\n");

        var preview = RecordFormat.Output(stored, limit: 7);

        Assert.Equal(new OutputPreview("first\ns", stored.Length - 7), preview);
    }

    [Fact]
    public void A_line_too_long_to_wrap_quickly_is_shown_shortened_with_one_marker()
    {
        var limit = RecordFormat.OutputShownLineGraphemes;
        var fits = new string('a', limit);
        var tooLong = new string('b', limit) + "c";

        var preview = RecordFormat.Output(Encoding.UTF8.GetBytes($"{fits}\n{tooLong}\nshort\n"));

        Assert.Equal(new OutputPreview($"{fits}\n{new string('b', limit)}…\nshort", 0, LinesShortened: true), preview);
        Assert.False(RecordFormat.Output(Encoding.UTF8.GetBytes(fits))!.LinesShortened);
    }

    [Fact]
    public void The_cut_never_splits_a_character()
    {
        // "aé" is a, then é in two bytes; a limit of 2 falls inside é, so the cut backs off before it.
        var stored = Encoding.UTF8.GetBytes("aé");

        var preview = RecordFormat.Output(stored, limit: 2);

        Assert.Equal(new OutputPreview("a", 2), preview);
    }

    [Fact]
    public void The_default_limit_is_one_megabyte()
    {
        // Lines of 1 KB, so only the byte limit shortens the view.
        var line = new string('x', 1023) + "\n";
        var stored = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(line, RecordFormat.OutputShownBytes / 1024)) + "0123456789");

        var preview = RecordFormat.Output(stored)!;

        Assert.Equal(RecordFormat.OutputShownBytes - 1, preview.Text.Length);
        Assert.Equal(10, preview.MoreBytes);
        Assert.False(preview.LinesShortened);
    }
}
