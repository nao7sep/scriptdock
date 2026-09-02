using System;
using System.IO;
using Xunit;

namespace ScriptDock.Tests;

public sealed class FailurePresentationTests
{
    private const string Hostile = "EACCES Error invoking remote method IPC /private/tmp/hostile-sentinel";

    [Fact]
    public void ArbitraryDiagnosticsNeverBecomePresentationCopy()
    {
        var error = new IOException(Hostile, new InvalidOperationException("root cause"));

        var messages = new[]
        {
            FailurePresentation.StartupStorage(),
            FailurePresentation.StartupData(),
            FailurePresentation.RecoveredData(),
            FailurePresentation.RootPicker(error),
            FailurePresentation.ScriptStart(error),
        };

        Assert.All(messages, message => Assert.DoesNotContain(Hostile, message, StringComparison.Ordinal));
        Assert.NotNull(error.InnerException);
    }

    [Fact]
    public void PermissionFailureUsesStructuredRecovery()
    {
        var message = FailurePresentation.ScriptStart(new UnauthorizedAccessException(Hostile));

        Assert.Contains("can run it", message, StringComparison.Ordinal);
        Assert.DoesNotContain(Hostile, message, StringComparison.Ordinal);
    }
}
