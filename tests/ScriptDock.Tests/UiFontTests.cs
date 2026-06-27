using System.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using ScriptDock;
using ScriptDock.Models;
using Xunit;

namespace ScriptDock.Tests;

/// <summary>
/// The UI-font resolver turns the free-text (possibly comma-separated) chrome-font setting into a
/// concrete family: the first installed family wins, otherwise the bundled default (Inter).
/// </summary>
public sealed class UiFontTests
{
    [Fact]
    public void ParseFamilies_SplitsTrimsStripsQuotesAndDropsEmpties()
    {
        Assert.Equal(
            new[] { "Helvetica Neue", "Segoe UI", "Roboto" },
            UiFont.ParseFamilies("\"Helvetica Neue\", Segoe UI , , 'Roboto'").ToArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseFamilies_YieldsNothingForBlank(string? value)
    {
        Assert.Empty(UiFont.ParseFamilies(value));
    }

    [AvaloniaFact]
    public void Resolve_FallsBackToBundledDefaultWhenNothingMatches()
    {
        Assert.Equal(AppConfig.DefaultUiFontFamily, UiFont.Resolve("No Such Font 99999").Name);
        Assert.Equal(AppConfig.DefaultUiFontFamily, UiFont.Resolve("").Name);
        Assert.Equal(AppConfig.DefaultUiFontFamily, UiFont.Resolve(null).Name);
    }

    [AvaloniaFact]
    public void Resolve_PrefersTheFirstInstalledFamily()
    {
        var installed = FontManager.Current.SystemFonts.FirstOrDefault();
        if (installed is null)
        {
            // No system fonts in this environment; the fallback path is covered above.
            return;
        }

        // An absent family listed first is skipped in favor of the installed one.
        Assert.Equal(installed.Name, UiFont.Resolve($"No Such Font 99999, {installed.Name}").Name);
    }
}
