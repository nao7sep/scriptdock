using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace ScriptDock.Tests.Services;

// Execute the real packaging script in a disposable repository. Native command stand-ins exercise
// failure propagation without publishing the app, downloading tools or invoking an installed compiler.
public sealed class WindowsPackagingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scriptdock-package-" + Guid.NewGuid().ToString("N"));

    public WindowsPackagingTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "scripts"));
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        File.Copy(SourceScan.RepoFile("scripts", "package.ps1"), Path.Combine(_root, "scripts", "package.ps1"));
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"),
            "<Project><PropertyGroup><Product>Fixture</Product><Company>Fixture</Company><Version>0.0.0</Version></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(_root, "LICENSE"), "fixture license");
        File.WriteAllText(Path.Combine(_root, "bin", "iscc.cmd"), "@echo off\r\necho called>compiler-called\r\nexit /b 19\r\n");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [WindowsOnlyFact]
    public async Task FailedPublishStopsBeforePackagingOrInvokingTheInstallerCompiler()
    {
        File.WriteAllText(Path.Combine(_root, "bin", "dotnet.cmd"), "@echo off\r\nexit /b 17\r\n");
        var result = await Run();
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("dotnet publish failed with exit code 17", result.Error);
        Assert.False(File.Exists(Path.Combine(_root, "compiler-called")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "dist")));
    }

    [WindowsOnlyFact]
    public async Task FailedInstallerCompilerFailsThePackageCommandWithoutListingSuccess()
    {
        File.WriteAllText(Path.Combine(_root, "bin", "dotnet.cmd"),
            "@echo off\r\nmkdir publish-win\r\necho fixture>publish-win\\ScriptDock.exe\r\nexit /b 0\r\n");
        var result = await Run();
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Inno Setup compiler failed with exit code 19", result.Error);
        Assert.True(File.Exists(Path.Combine(_root, "compiler-called")));
        Assert.True(File.Exists(Path.Combine(_root, "dist", "Fixture-0.0.0-win.zip")));
        Assert.DoesNotContain("Fixture-0.0.0-win.zip", result.Output);
    }

    private async Task<(int ExitCode, string Output, string Error)> Run()
    {
        var start = new ProcessStartInfo("pwsh")
        {
            WorkingDirectory = _root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(Path.Combine(_root, "scripts", "package.ps1"));
        start.Environment["PATH"] = Path.Combine(_root, "bin") + Path.PathSeparator + start.Environment["PATH"];
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            return (process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(System.Threading.CancellationToken.None);
            }
        }
    }
}
