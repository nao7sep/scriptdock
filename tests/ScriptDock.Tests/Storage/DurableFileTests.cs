using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using ScriptDock.Storage;
using ScriptDock.Tests.Fakes;
using Xunit;

namespace ScriptDock.Tests.Storage;

// These are native filesystem publication checks. They verify the available success and failure
// boundaries, rather than pretending an ordinary test run can simulate power loss.
public sealed class DurableFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scriptdock-durable-tests", NanoId.New());

    public DurableFileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void Publish_FreshFilePublishesExactBytesAndConsumesStaging()
    {
        var staging = Path.Combine(_root, "config-staging.tmp");
        var destination = Path.Combine(_root, "config.json");
        byte[] content = [0x00, 0xFF, 0xEF, 0xBB, 0xBF, (byte)'\r', (byte)'\n'];
        File.WriteAllBytes(staging, content);

        DurableFile.Publish(staging, destination);

        Assert.Equal(content, File.ReadAllBytes(destination));
        Assert.False(File.Exists(staging));
        Assert.Single(Directory.GetFiles(_root));
    }

    [MacOnlyFact]
    [SupportedOSPlatform("macos")]
    public void Publish_ReplacementKeepsTheDestinationsUnixMode()
    {
        var staging = Path.Combine(_root, "config-staging.tmp");
        var destination = Path.Combine(_root, "config.json");
        File.WriteAllText(destination, "original");
        File.WriteAllText(staging, "replacement");
        const UnixFileMode originalMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        File.SetUnixFileMode(destination, originalMode);
        File.SetUnixFileMode(staging, originalMode | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        DurableFile.Publish(staging, destination);

        Assert.Equal("replacement", File.ReadAllText(destination));
        Assert.Equal(originalMode, File.GetUnixFileMode(destination));
        Assert.False(File.Exists(staging));
    }

    [WindowsOnlyFact]
    [SupportedOSPlatform("windows")]
    public void Publish_ReplacementKeepsTheDestinationsAccessRules()
    {
        var staging = Path.Combine(_root, "config-staging.tmp");
        var destination = Path.Combine(_root, "config.json");
        File.WriteAllText(destination, "original");
        File.WriteAllText(staging, "replacement");
        using var identity = WindowsIdentity.GetCurrent();
        var access = new FileSecurity();
        access.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        access.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(destination).SetAccessControl(access);
        var original = new FileInfo(destination).GetAccessControl(AccessControlSections.Access)
            .GetSecurityDescriptorSddlForm(AccessControlSections.Access);

        DurableFile.Publish(staging, destination);

        Assert.Equal("replacement", File.ReadAllText(destination));
        Assert.Equal(original, new FileInfo(destination).GetAccessControl(AccessControlSections.Access)
            .GetSecurityDescriptorSddlForm(AccessControlSections.Access));
    }

    [Fact]
    public void Publish_FailedReplacementLeavesOriginalAndStagingIntact()
    {
        var staging = Path.Combine(_root, "config-staging.tmp");
        var destination = Path.Combine(_root, "config.json");
        File.WriteAllText(destination, "original");
        File.WriteAllText(staging, "replacement");
        FileStream? held = null;
        if (OperatingSystem.IsWindows())
            held = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
        else if (OperatingSystem.IsMacOS())
            File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var error = Record.Exception(() => DurableFile.Publish(staging, destination));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.Equal("original", File.ReadAllText(destination));
            Assert.Equal("replacement", File.ReadAllText(staging));
        }
        finally
        {
            held?.Dispose();
            if (OperatingSystem.IsMacOS())
                File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
