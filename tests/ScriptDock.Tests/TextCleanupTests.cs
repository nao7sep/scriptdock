using Xunit;

namespace ScriptDock.Tests;

public sealed class TextCleanupTests
{
    [Theory]
    [InlineData("  hello  ", "hello")]
    [InlineData("a\nb", "a b")]
    [InlineData("aaa\n \n\nbbb", "aaa bbb")]
    [InlineData("a    b", "a    b")]
    [InlineData("a　b", "a　b")]
    [InlineData("\n\n  \n", "")]
    public void SingleLine_MatchesTheReferenceCases(string text, string expected) =>
        Assert.Equal(expected, TextCleanup.SingleLine(text));

    [Theory]
    [InlineData("hello world", 5, "hello", true)]
    [InlineData("hello", 5, "hello", false)]
    [InlineData("hello", 10, "hello", false)]
    [InlineData("  hello  ", 3, "hel", true)]
    [InlineData("hello   ", 5, "hello", false)]
    [InlineData("a\nb\nc", 1, "a", true)]
    [InlineData("one two three", 5, "one t", true)]
    [InlineData("\U0001F600x", 1, "\U0001F600", true)]
    [InlineData("\U0001F468\u200D\U0001F469\u200D\U0001F467x", 1, "\U0001F468\u200D\U0001F469\u200D\U0001F467", true)]
    [InlineData("e\u0301llo", 2, "e\u0301l", true)]
    [InlineData("", 5, "", false)]
    [InlineData("hello", 0, "", false)]
    [InlineData("   ", 5, "", false)]
    public void Truncate_MatchesTheReferenceCases(string text, int n, string expected, bool truncated) =>
        Assert.Equal(new TextCleanup.TruncateResult(expected, truncated), TextCleanup.Truncate(text, n));
}
