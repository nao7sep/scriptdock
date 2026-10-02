using System;
using ScriptDock.Models;
using Xunit;

namespace ScriptDock.Tests.Models;

public sealed class ConfigDefaultsTests
{
    [Fact]
    public void DefaultExtension_MatchesPlatform()
    {
        var expected = OperatingSystem.IsWindows() ? ".ps1" : ".command";

        Assert.Equal(expected, ConfigDefaults.DefaultExtension);
    }
}
