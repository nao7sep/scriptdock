using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using ScriptDock.Services;
using Xunit;

namespace ScriptDock.Tests.Services;

public sealed class LauncherRuntimeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "scriptdock-launcher-" + Guid.NewGuid().ToString("N"));
    private string Registration => Path.Combine(_directory, "launcher-runtime-process");
    private string Owner => Path.Combine(_directory, "launcher-runtime-owner");

    public LauncherRuntimeTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Explicit_launch_registers_exact_process_or_activation_without_claiming_the_existing_instance(bool ownsInstance)
    {
        File.WriteAllText(Owner, "current\n");
        LauncherRuntime.Register(["--launcher-registration", Registration, "--launcher-token", "current"], ownsInstance);

        var fields = File.ReadAllLines(Registration);
        using var process = Process.GetCurrentProcess();
        Assert.Equal(3, fields.Length);
        Assert.Equal("current", fields[0]);
        Assert.Equal(ownsInstance ? process.Id : 0, int.Parse(fields[1], CultureInfo.InvariantCulture));
        Assert.Equal(new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeSeconds(),
            long.Parse(fields[2], CultureInfo.InvariantCulture));
        Assert.Equal(2, Directory.GetFiles(_directory).Length);
    }

    [Fact]
    public void Ordinary_startup_without_explicit_registration_writes_nothing()
    {
        LauncherRuntime.Register([], ownsInstance: true);
        LauncherRuntime.Register(["--launcher-token", "current"], ownsInstance: true);
        LauncherRuntime.Register(["--launcher-registration", Registration], ownsInstance: true);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public void Superseded_launch_does_not_replace_the_current_registration()
    {
        File.WriteAllText(Owner, "current\n");
        File.WriteAllText(Registration, "current\n0\n0\n");
        LauncherRuntime.Register(["--launcher-registration", Registration, "--launcher-token", "older"], ownsInstance: true);
        Assert.Equal("current\n0\n0\n", File.ReadAllText(Registration));
    }

    [Fact]
    public void Missing_registration_directory_does_not_get_created_or_prevent_app_startup()
    {
        var missing = Path.Combine(_directory, "missing", "launcher-runtime-process");
        LauncherRuntime.Register(["--launcher-registration", missing, "--launcher-token", "current"], ownsInstance: true);
        Assert.False(Directory.Exists(Path.GetDirectoryName(missing)));
    }

    [Fact]
    public void Failed_registration_leaves_existing_destination_intact_and_removes_staging_file()
    {
        File.WriteAllText(Owner, "current\n");
        Directory.CreateDirectory(Registration);
        LauncherRuntime.Register(["--launcher-registration", Registration, "--launcher-token", "current"], ownsInstance: true);
        Assert.True(Directory.Exists(Registration));
        Assert.Single(Directory.GetFiles(_directory));
    }
}
