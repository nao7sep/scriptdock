using ScriptDock.Services;
using Xunit;

namespace ScriptDock.Tests.Services;

public sealed class OutputBufferTests
{
    [Fact]
    public void AppendLine_StripsAnsi()
    {
        var buffer = new OutputBuffer();
        buffer.AppendLine("[32mok[0m");

        Assert.Equal(["ok"], buffer.Snapshot());
    }

    [Fact]
    public void Buffer_CapsLinesAndCountsDropped()
    {
        var buffer = new OutputBuffer(maxLines: 3);
        for (var i = 0; i < 5; i++)
            buffer.AppendLine($"line {i}");

        Assert.Equal(["line 2", "line 3", "line 4"], buffer.Snapshot());
        Assert.Equal(2, buffer.DroppedCount);
    }
}
