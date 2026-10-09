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
            FailurePresentation.StartupData("/x/config.json"),
            FailurePresentation.RecoveredData("/x/config-20260101-000000-utc.invalid"),
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
    public void RecoveryNoticesNameTheActualFile()
    {
        const string preserved = "/Users/me/.scriptdock/config-20260101-000000-utc.invalid";
        const string halted = "/Users/me/.scriptdock/config.json";

        Assert.Contains(preserved, English.Of(FailurePresentation.RecoveredData(preserved)), StringComparison.Ordinal);
        Assert.StartsWith(halted + " could not be read", English.Of(FailurePresentation.StartupData(halted)), StringComparison.Ordinal);
    }

    [Fact]
    public void KeptSettingsNoticeNamesTheFileAndEveryKeptKey()
    {
        const string path = "/Users/me/.scriptdock/config.json";

        var text = English.Of(FailurePresentation.KeptSettings(path, ["extensions", "future"]));

        Assert.Contains(path, text, StringComparison.Ordinal);
        Assert.Contains("extensions, future", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordsNoticeNamesTheDatabase()
    {
        const string path = "/Users/me/.scriptdock/records.sqlite3";

        Assert.StartsWith(path + " could not be opened", English.Of(FailurePresentation.RecordsUnavailable(path)), StringComparison.Ordinal);
    }

    [Fact]
    public void PermissionFailureUsesStructuredRecovery()
    {
        var message = FailurePresentation.ScriptStart(new UnauthorizedAccessException(Hostile));

        Assert.Contains("can run it", English.Of(message), StringComparison.Ordinal);
        Assert.DoesNotContain(Hostile, English.Of(message), StringComparison.Ordinal);
    }
}
