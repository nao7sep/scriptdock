using System.Linq;
using ScriptDock;
using Xunit;

namespace ScriptDock.Tests;

/// <summary>
/// Locks the nanoid generator's shape: the default length, the URL-safe alphabet it draws
/// from, and that two calls do not collide — the properties every <c>Guid.NewGuid()</c>
/// call in this app relied on before it was replaced by <see cref="NanoId.New"/>.
/// </summary>
public sealed class NanoIdTests
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-";

    [Fact]
    public void New_DefaultLength_Is21()
    {
        Assert.Equal(21, NanoId.New().Length);
    }

    [Fact]
    public void New_CustomLength_IsHonored()
    {
        Assert.Equal(10, NanoId.New(10).Length);
    }

    [Fact]
    public void New_OnlyUsesUrlSafeAlphabet()
    {
        var id = NanoId.New(500);
        Assert.True(id.All(c => Alphabet.Contains(c)));
    }

    [Fact]
    public void New_TwoCalls_Differ()
    {
        Assert.NotEqual(NanoId.New(), NanoId.New());
    }
}
