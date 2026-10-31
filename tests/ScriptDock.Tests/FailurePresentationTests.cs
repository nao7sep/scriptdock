using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ScriptDock.Storage;
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
            FailurePresentation.BackupsUnavailable("/x/backups.sqlite3"),
            FailurePresentation.RootPicker(error),
            FailurePresentation.ScriptStart(error),
        };

        var all = new List<ScriptDock.I18n.Message>(messages);
        all.AddRange(FailurePresentation.LaunchStopped([new RefusedFile("/x/config.json", Newer: false)]));
        Assert.All(all, message => Assert.DoesNotContain(Hostile, English.Of(message), StringComparison.Ordinal));
        Assert.NotNull(error.InnerException);
    }

    // One paragraph per refused file, each naming it and saying whether a newer version wrote it or it could
    // not be read, then what to do.
    [Fact]
    public void LaunchStopNamesEveryRefusedFileAndWhy()
    {
        const string config = "/Users/me/.scriptdock/config.json";
        const string records = "/Users/me/.scriptdock/records.sqlite3";

        var text = FailurePresentation.LaunchStopped([new RefusedFile(config, Newer: false), new RefusedFile(records, Newer: true)])
            .Select(English.Of).ToList();

        Assert.Equal(3, text.Count);
        Assert.Equal(config + " could not be read.", text[0]);
        Assert.StartsWith(records + " was saved by a newer version", text[1], StringComparison.Ordinal);
        Assert.Contains("use that version", text[2], StringComparison.Ordinal);
        Assert.Contains("move it away to start fresh", text[2], StringComparison.Ordinal);
    }

    [Fact]
    public void BackupsNoticeNamesTheDatabase()
    {
        const string path = "/Users/me/.scriptdock/backups.sqlite3";

        Assert.StartsWith(path + " could not be opened", English.Of(FailurePresentation.BackupsUnavailable(path)), StringComparison.Ordinal);
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
