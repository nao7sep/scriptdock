using ScriptDock.Services;
using Xunit;

namespace ScriptDock.Tests.Services;

public sealed class IgnoreRulesTests
{
    [Fact]
    public void SlashWrappedPattern_MatchesDirectoryPathWithTrailingSlash()
    {
        var rules = IgnoreRules.Compile(["/node_modules/"]);

        Assert.Equal("/node_modules/", rules.FirstMatch("/Users/x/proj/node_modules/"));
        Assert.Null(rules.FirstMatch("/Users/x/proj/src/"));
    }

    [Fact]
    public void Pattern_MatchesFilePath()
    {
        var rules = IgnoreRules.Compile(["/bin/"]);

        Assert.Equal("/bin/", rules.FirstMatch("/Users/x/proj/bin/tool.command"));
    }

    [Fact]
    public void Matching_NormalisesBackslashesAndIsCaseInsensitive()
    {
        var rules = IgnoreRules.Compile(["/node_modules/"]);

        Assert.Equal("/node_modules/", rules.FirstMatch(@"C:\x\Node_Modules\pkg\"));
    }

    [Fact]
    public void InvalidPattern_IsCollected_NotThrown()
    {
        var rules = IgnoreRules.Compile(["[", "/obj/"]);

        Assert.Contains("[", rules.InvalidPatterns);
        Assert.Equal("/obj/", rules.FirstMatch("/x/obj/"));
    }

    [Fact]
    public void NoMatch_ReturnsNull()
    {
        var rules = IgnoreRules.Compile(["/obj/"]);

        Assert.Null(rules.FirstMatch("/x/src/main.command"));
    }
}
