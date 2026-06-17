using ScriptDock.Services;
using Xunit;

namespace ScriptDock.Tests.Services;

public sealed class ShellCommandTests
{
    [Fact]
    public void Unix_RunsScriptThroughLoginZsh_QuotedPath()
    {
        var command = ShellCommand.For("/Users/x/proj/scripts/run-dev.command", ScriptPlatform.Unix);

        Assert.Equal("zsh", command.FileName);
        Assert.Equal(["-l", "-c", "'/Users/x/proj/scripts/run-dev.command'"], command.Arguments);
    }

    [Fact]
    public void Unix_QuotesSpacesAndSingleQuotes()
    {
        var command = ShellCommand.For("/Users/x/it's a/run.command", ScriptPlatform.Unix);

        Assert.Equal("'/Users/x/it'\\''s a/run.command'", command.Arguments[2]);
    }

    [Fact]
    public void Windows_Ps1_RunsUnderPwsh()
    {
        var command = ShellCommand.For(@"C:\x\scripts\run-dev.ps1", ScriptPlatform.Windows);

        Assert.Equal("pwsh", command.FileName);
        Assert.Equal(["-NoLogo", "-File", @"C:\x\scripts\run-dev.ps1"], command.Arguments);
    }

    [Fact]
    public void Windows_Bat_RunsUnderCmd()
    {
        var command = ShellCommand.For(@"C:\x\scripts\task.bat", ScriptPlatform.Windows);

        Assert.Equal("cmd", command.FileName);
        Assert.Equal(["/c", @"C:\x\scripts\task.bat"], command.Arguments);
    }
}
