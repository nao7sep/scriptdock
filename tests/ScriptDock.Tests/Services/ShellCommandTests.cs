using ScriptDock.Services;
using Xunit;

namespace ScriptDock.Tests.Services;

public sealed class ShellCommandTests
{
    [Fact]
    public void Unix_RedirectsOutputToLog_ThroughLoginZsh()
    {
        var command = ShellCommand.ForRun("/proj/scripts/run-dev.command", "/logs/run.log", ScriptPlatform.Unix);

        Assert.Equal("zsh", command.FileName);
        Assert.Equal(
            ["-l", "-c", "'/proj/scripts/run-dev.command' > '/logs/run.log' 2>&1"],
            command.Arguments);
    }

    [Fact]
    public void Unix_QuotesSpacesAndSingleQuotes()
    {
        var command = ShellCommand.ForRun("/a b/it's.command", "/l/o g.log", ScriptPlatform.Unix);

        Assert.Equal(
            "'/a b/it'\\''s.command' > '/l/o g.log' 2>&1",
            command.Arguments[2]);
    }

    [Fact]
    public void Windows_Ps1_RunsUnderPwsh_AllStreamsToLog()
    {
        var command = ShellCommand.ForRun(@"C:\x\run-dev.ps1", @"C:\logs\run.log", ScriptPlatform.Windows);

        Assert.Equal("pwsh", command.FileName);
        Assert.Equal(["-NoLogo", "-Command", @"& 'C:\x\run-dev.ps1' *> 'C:\logs\run.log'"], command.Arguments);
    }

    [Fact]
    public void Windows_Bat_RunsUnderPwsh_LikeEveryWindowsScript()
    {
        // .bat/.cmd go through pwsh too (not cmd), so the path is a single-quoted argument and a
        // %VAR% token in it is taken literally instead of being expanded by cmd.
        var command = ShellCommand.ForRun(@"C:\x\task.bat", @"C:\logs\run.log", ScriptPlatform.Windows);

        Assert.Equal("pwsh", command.FileName);
        Assert.Equal(["-NoLogo", "-Command", @"& 'C:\x\task.bat' *> 'C:\logs\run.log'"], command.Arguments);
    }

    [Fact]
    public void Windows_PercentTokenInPath_IsTakenLiterally_NotCmdExpanded()
    {
        // A legal NTFS filename containing %VAR%: single-quoted in pwsh, so it is never expanded.
        var command = ShellCommand.ForRun(@"C:\scripts\%PATH%.bat", @"C:\logs\run.log", ScriptPlatform.Windows);

        Assert.Equal("pwsh", command.FileName);
        Assert.Equal(@"& 'C:\scripts\%PATH%.bat' *> 'C:\logs\run.log'", command.Arguments[2]);
    }
}
