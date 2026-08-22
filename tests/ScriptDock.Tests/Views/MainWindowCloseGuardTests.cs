using Avalonia.Controls;
using ScriptDock.Views;
using Xunit;

namespace ScriptDock.Tests.Views;

public sealed class MainWindowCloseGuardTests
{
    [Fact]
    public void OnlyDirectWindowCloseMayPrompt()
    {
        Assert.True(MainWindowCloseGuard.ShouldConfirmQuit(WindowCloseReason.WindowClosing, true));
        Assert.False(MainWindowCloseGuard.ShouldConfirmQuit(WindowCloseReason.ApplicationShutdown, true));
        Assert.False(MainWindowCloseGuard.ShouldConfirmQuit(WindowCloseReason.OSShutdown, true));
        Assert.False(MainWindowCloseGuard.ShouldConfirmQuit(WindowCloseReason.OwnerWindowClosing, true));
        Assert.False(MainWindowCloseGuard.ShouldConfirmQuit(WindowCloseReason.WindowClosing, false));
    }
}
