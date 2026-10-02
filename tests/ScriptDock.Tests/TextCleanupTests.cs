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
}
