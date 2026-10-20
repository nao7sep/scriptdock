using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScriptDock.I18n;
using Xunit;

namespace ScriptDock.Tests.I18n;

/// <summary>
/// The hard-coded-text gate (localization conventions): no sentence a reader sees is written in the
/// source, and no key reaches the source that the catalogue does not have.
///
/// This reads the shipped source as text, which is what a test of a file rather than a module does
/// (tests-folder conventions). It is deliberately narrow: it looks at the properties a person reads
/// or hears — text, a button's content, a menu's header, a window title, a placeholder, a tooltip and
/// the two automation properties — and leaves everything else alone.
/// </summary>
public class HardCodedTextTests
{
    /// <summary>
    /// Literals allowed in a text position: the app's own name, and the two placeholders that show an
    /// example value rather than words, a font family and a file extension, which read the same in every
    /// language. Text with no letters, such as a separator, is never a sentence and needs no entry.
    /// </summary>
    private static readonly string[] AllowedLiterals = ["ScriptDock", "Inter", ".command"];

    private static readonly string[] TextProperties =
        ["Text", "Content", "Header", "Title", "PlaceholderText", "Watermark"];

    [Fact]
    public void no_markup_holds_a_sentence_of_its_own()
    {
        var offenders = new List<string>();
        foreach (var file in SourceFiles(".axaml"))
            MarkupOffenders(offenders, [], Path.GetFileName(file), File.ReadAllText(file));
        Assert.Empty(offenders);
    }

    [Fact]
    public void no_code_assigns_a_sentence_to_a_property_a_reader_sees()
    {
        var offenders = new List<string>();
        foreach (var file in SourceFiles(".cs"))
            CodeOffenders(offenders, [], Path.GetFileName(file), File.ReadAllText(file));
        Assert.Empty(offenders);
    }

    // Each scan can fail: a planted sentence in each position it reads is reported.
    [Fact]
    public void the_scans_report_a_planted_sentence()
    {
        var markup = new List<string>();
        MarkupOffenders(markup, [], "planted.axaml",
            """<TextBlock Text="Save your work"/><Button ToolTip.Tip="Close the window" AutomationProperties.Name="Open the file"/>""");
        Assert.Equal(3, markup.Count);

        var code = new List<string>();
        CodeOffenders(code, [], "planted.cs",
            """Title = "Open the file"; AutomationProperties.SetName(button, "Run it now"); ToolTip.SetTip(button, $"Stop {x}");""");
        Assert.Equal(3, code.Count);

        var missing = new List<string>();
        MissingKeys(missing, Keys(), "planted.cs", """Message.Of("no.such.key")""");
        Assert.Single(missing);
    }

    // An allowed literal no text position uses any more is an exception nothing needs.
    [Fact]
    public void every_allowed_literal_is_still_used()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in SourceFiles(".axaml"))
            MarkupOffenders([], used, Path.GetFileName(file), File.ReadAllText(file));
        foreach (var file in SourceFiles(".cs"))
            CodeOffenders([], used, Path.GetFileName(file), File.ReadAllText(file));
        Assert.All(AllowedLiterals, literal => Assert.Contains(literal, used));
    }

    private static void MarkupOffenders(List<string> offenders, HashSet<string> allowed, string name, string markup)
    {
        foreach (var property in TextProperties)
        {
            foreach (Match match in Regex.Matches(markup, $@"(?<![\w.]){property}\s*=\s*""([^""]*)"""))
                Record(offenders, allowed, name, property, match.Groups[1].Value);
        }

        foreach (Match match in Regex.Matches(markup, @"ToolTip\.Tip\s*=\s*""([^""]*)"""))
            Record(offenders, allowed, name, "ToolTip.Tip", match.Groups[1].Value);

        foreach (Match match in Regex.Matches(markup, @"AutomationProperties\.(Name|HelpText)\s*=\s*""([^""]*)"""))
            Record(offenders, allowed, name, "AutomationProperties." + match.Groups[1].Value, match.Groups[2].Value);
    }

    private static void CodeOffenders(List<string> offenders, HashSet<string> allowed, string name, string code)
    {
        // Text = "…", Content = "…", Title = "…" and their kin, including an interpolated string,
        // which is how a sentence used to be assembled here.
        foreach (var property in TextProperties)
        {
            foreach (Match match in Regex.Matches(code, $@"(?<![\w.]){property}\s*=\s*\$?""([^""]*)"""))
                Record(offenders, allowed, name, property, match.Groups[1].Value);
        }

        foreach (Match match in Regex.Matches(code, @"AutomationProperties\.SetName\([^,]+,\s*\$?""([^""]*)"""))
            Record(offenders, allowed, name, "AutomationProperties.SetName", match.Groups[1].Value);

        foreach (Match match in Regex.Matches(code, @"ToolTip\.SetTip\([^,]+,\s*\$?""([^""]*)"""))
            Record(offenders, allowed, name, "ToolTip.SetTip", match.Groups[1].Value);
    }

    private static IEnumerable<string> SourceFiles(string extension) =>
        Directory.GetFiles(SourceDirectory(), "*" + extension, SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    [Fact]
    public void every_key_the_source_names_is_in_the_catalogue()
    {
        var english = Keys();
        var missing = new List<string>();
        foreach (var file in SourceFiles(".cs").Concat(SourceFiles(".axaml")))
            MissingKeys(missing, english, Path.GetFileName(file), File.ReadAllText(file));
        Assert.Empty(missing);
    }

    private static readonly string[] KeyPatterns =
    {
        @"Localized\.[A-Za-z]+\s*=\s*""([a-zA-Z][a-zA-Z.]*)""",
        @"Localized\.Set[A-Za-z]+\([^,]+,\s*""([a-zA-Z][a-zA-Z.]*)""",
        @"Message\.Of\(\s*""([a-zA-Z][a-zA-Z.]*)""",
        @"(?:Localizer|t)\.T\(\s*""([a-zA-Z][a-zA-Z.]*)""",
        @"DialogButton\(\s*""([a-zA-Z][a-zA-Z.]*)""",
    };

    private static void MissingKeys(List<string> missing, HashSet<string> english, string name, string text)
    {
        foreach (var pattern in KeyPatterns)
        {
            foreach (Match match in Regex.Matches(text, pattern))
            {
                var key = match.Groups[1].Value;
                if (!key.Contains('.') || english.Contains(key))
                    continue;
                missing.Add($"{name}: {key}");
            }
        }
    }

    [Fact]
    public void every_key_in_the_catalogue_is_used()
    {
        // A key nothing names is dead weight every translator still has to translate.
        var text = string.Join(
            "\n",
            Directory.GetFiles(SourceDirectory(), "*.*", SearchOption.AllDirectories)
                .Where(file => Path.GetExtension(file) is ".cs" or ".axaml")
                .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                .Select(File.ReadAllText));

        // The language names are named by the language list itself, not one literal each.
        var named = Languages.Tags.Select(Languages.InterfaceNameKey).ToHashSet(StringComparer.Ordinal);
        var unused = Keys().Where(key => !named.Contains(key) && !text.Contains($"\"{key}\"", StringComparison.Ordinal)).ToArray();
        Assert.Empty(unused);
    }

    private static void Record(List<string> offenders, HashSet<string> allowed, string file, string property, string value)
    {
        var text = value.Trim();
        // A binding, a resource, a static reference, a number or a separator is not a sentence.
        if (text.Length == 0 || text.StartsWith('{') || text.StartsWith('/') || !text.Any(char.IsLetter))
            return;
        if (AllowedLiterals.Contains(text))
        {
            allowed.Add(text);
            return;
        }
        // A single lower-case token is a name or a value, not a sentence a reader reads.
        if (!text.Any(char.IsWhiteSpace) && char.IsLower(text[0]) && !text.Contains('’'))
            return;

        offenders.Add($"{file}: {property}=\"{value}\"");
    }

    private static HashSet<string> Keys()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(CatalogueTests.LocalesDirectory(), "en.json")));
        return document.RootElement.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
    }

    private static string SourceDirectory() => Path.Combine(CatalogueTests.RepoRoot(), "src", "ScriptDock");
}
