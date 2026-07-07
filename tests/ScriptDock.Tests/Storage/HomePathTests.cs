using System;
using System.IO;
using ScriptDock.Storage;
using Xunit;

namespace ScriptDock.Tests.Storage;

/// <summary>
/// The shared home-anchored path rule used by both the storage-root override and the scan-root
/// editor: expand a leading <c>~</c>, and make a relative value absolute against the home
/// directory — never the working directory.
/// </summary>
public sealed class HomePathTests
{
    // An OS-appropriate, already-normalised home so expectations don't depend on the test machine.
    private static readonly string Home =
        Path.GetFullPath(OperatingSystem.IsWindows() ? @"C:\home\tester" : "/home/tester");

    [Fact]
    public void Tilde_ExpandsToHome()
    {
        Assert.Equal(Home, HomePath.AnchorToHome("~", Home));
    }

    [Fact]
    public void TildeSlash_ExpandsBeneathHome()
    {
        Assert.Equal(Path.Combine(Home, "code", "scripts"), HomePath.AnchorToHome("~/code/scripts", Home));
    }

    [Fact]
    public void RelativeValue_AnchorsToHome_NotWorkingDirectory()
    {
        Assert.Equal(Path.Combine(Home, "projects"), HomePath.AnchorToHome("projects", Home));
    }

    [Fact]
    public void AbsoluteValue_IsReturnedAsFullPath()
    {
        var absolute = Path.GetFullPath(OperatingSystem.IsWindows() ? @"D:\elsewhere\scripts" : "/elsewhere/scripts");
        Assert.Equal(absolute, HomePath.AnchorToHome(absolute, Home));
    }
}
