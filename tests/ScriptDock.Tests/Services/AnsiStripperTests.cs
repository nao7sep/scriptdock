using ScriptDock.Services;
using Xunit;

namespace ScriptDock.Tests.Services;

public sealed class AnsiStripperTests
{
    [Fact]
    public void Strip_RemovesColorCodes()
    {
        Assert.Equal("red", AnsiStripper.Strip("[31mred[0m"));
    }

    [Fact]
    public void Strip_RemovesCursorAndEraseCodes()
    {
        Assert.Equal("hello", AnsiStripper.Strip("[2K[1Ghello"));
    }

    [Fact]
    public void Strip_LeavesPlainTextUnchanged()
    {
        Assert.Equal("npm run dev", AnsiStripper.Strip("npm run dev"));
    }
}
