using System;
using System.Diagnostics;
using ScriptDock.Services;
using Xunit;

namespace ScriptDock.Tests.Services;

public sealed class ExternalLauncherTests
{
    [Fact]
    public void Open_reports_when_the_shell_did_not_accept_the_request()
    {
        Assert.False(ExternalLauncher.Open("https://example.test", _ => null));
    }

    [Fact]
    public void Open_logs_and_reports_a_launch_exception()
    {
        Assert.False(ExternalLauncher.Open(
            "https://example.test",
            _ => throw new InvalidOperationException("no browser")));
    }

    [Fact]
    public void Open_reports_success_when_the_shell_returns_a_process()
    {
        Assert.True(ExternalLauncher.Open("https://example.test", _ => new Process()));
    }
}
