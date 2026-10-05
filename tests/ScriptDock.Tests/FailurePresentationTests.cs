using System;
using System.IO;
using ScriptDock.Tests.I18n;
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

        Assert.All(messages, message => Assert.DoesNotContain(Hostile, English.Of(message), StringComparison.Ordinal));
        Assert.NotNull(error.InnerException);
    }

    [Fact]
    public void NewerStoreNamesTheFileByItsPath()
    {
        const string path = "/Users/me/.scriptdock/config.json";

        Assert.StartsWith(path + " was saved by a newer version", English.Of(FailurePresentation.NewerStore(path)), StringComparison.Ordinal);
    }

    [Fact]
    public void PermissionFailureUsesStructuredRecovery()
    {
        var message = FailurePresentation.ScriptStart(new UnauthorizedAccessException(Hostile));

        Assert.Contains("can run it", English.Of(message), StringComparison.Ordinal);
        Assert.DoesNotContain(Hostile, English.Of(message), StringComparison.Ordinal);
    }
}
