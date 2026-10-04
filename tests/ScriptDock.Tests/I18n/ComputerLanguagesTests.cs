using System;
using System.Diagnostics;
using System.Linq;
using ScriptDock.I18n;
using Xunit;

namespace ScriptDock.Tests.I18n;

/// <summary>
/// The computer's languages as macOS gives them. Shares the AppKit-language collection with the
/// bootstrap tests, whose volatile AppleLanguages would otherwise change what this reads.
/// </summary>
[Collection(AppKitLanguages.CollectionName)]
public sealed class ComputerLanguagesTests
{
    // The user's AppleLanguages as the defaults tool reads them, in a process of its own.
    private static string[] AppleLanguages()
    {
        using var defaults = Process.Start(new ProcessStartInfo("defaults", ["read", "-g", "AppleLanguages"])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        var output = defaults.StandardOutput.ReadToEnd();
        Assert.True(defaults.WaitForExit(TimeSpan.FromSeconds(20)));
        return output.Split(['(', ')', ',', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim().Trim('"'))
            .Where(item => item.Length > 0)
            .ToArray();
    }

    private static string LanguageOf(string tag) => tag.Split('-')[0];

    [MacOnlyFact]
    public void Read_gives_the_computers_preferred_languages_in_order()
    {
        var expected = AppleLanguages();

        var read = ComputerLanguages.Read();

        // macOS may add a region to a bare language (en → en-JP), so the languages and their order are
        // what must match, not the exact tags.
        Assert.Equal(expected.Select(LanguageOf), read.Select(LanguageOf));
    }
}
