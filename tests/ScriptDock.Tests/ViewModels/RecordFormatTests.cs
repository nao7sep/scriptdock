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
        Assert.Null(RecordFormat.OutputText(null));
        Assert.Null(RecordFormat.OutputText([]));
        Assert.Null(RecordFormat.OutputText(Encoding.UTF8.GetBytes(" \r\n\t\n")));
        Assert.Equal("done", RecordFormat.OutputText(Encoding.UTF8.GetBytes("done\n")));
    }
}
