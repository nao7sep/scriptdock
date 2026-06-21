using ScriptDock.Services;
using Xunit;

namespace ScriptDock.Tests.Services;

public sealed class AnsiStripperTests
{
    // Control chars built by code point so the test source stays plain ASCII (no \x / \u escapes,
    // whose length rules are a footgun next to hex-looking following characters).
    private const char Esc = (char)0x1b; // ESC, the escape-sequence introducer
    private const char Bel = (char)0x07; // BEL, an OSC terminator
    private const char Bs = (char)0x5c;  // backslash; ESC + backslash is the ST OSC terminator

    [Fact]
    public void Strip_RemovesColorCodes()
    {
        Assert.Equal("red", AnsiStripper.Strip(Esc + "[31mred" + Esc + "[0m"));
    }

    [Fact]
    public void Strip_RemovesCursorAndEraseCodes()
    {
        Assert.Equal("hello", AnsiStripper.Strip(Esc + "[2K" + Esc + "[1Ghello"));
    }

    [Fact]
    public void Strip_RemovesOscTitleSequence_TerminatedByBel()
    {
        // ESC ] 0 ; <title> BEL — the terminal-title sequence oh-my-zsh/git/npm emit.
        Assert.Equal("build done", AnsiStripper.Strip(Esc + "]0;my title" + Bel + "build done"));
    }

    [Fact]
    public void Strip_RemovesOscHyperlink_TerminatedBySt()
    {
        // ESC ] 8 ; ; <url> ST(ESC \) <text> ESC ] 8 ; ; ST — OSC 8 hyperlink wrapping link text.
        var input = Esc + "]8;;https://example.com" + Esc + Bs + "link" + Esc + "]8;;" + Esc + Bs;
        Assert.Equal("link", AnsiStripper.Strip(input));
    }

    [Fact]
    public void Strip_RemovesNonCsiEscapes()
    {
        // ESC ( B (charset designator), ESC = (keypad), ESC 7 (save cursor), ESC c (reset).
        Assert.Equal("ok", AnsiStripper.Strip(Esc + "(B" + Esc + "=" + Esc + "7" + Esc + "cok"));
    }

    [Fact]
    public void Strip_LeavesPlainTextUnchanged()
    {
        Assert.Equal("npm run dev", AnsiStripper.Strip("npm run dev"));
    }

    [Fact]
    public void Strip_LeavesSquareBracketsInPlainTextUnchanged()
    {
        // No ESC: a bare "]" or "[" is ordinary content and must survive.
        Assert.Equal("[info] done [0]", AnsiStripper.Strip("[info] done [0]"));
    }
}
