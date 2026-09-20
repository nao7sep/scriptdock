using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    /// Literals allowed in a text position: the app's own name, punctuation and separators, and the two
    /// placeholders that show an example value rather than words — a font family and a file extension,
    /// which read the same in every language.
    /// </summary>
    private static readonly string[] AllowedLiterals =
        ["ScriptDock", "Inter", ".command", "·", "—", "-", "/", ":", ""];

    private static readonly string[] TextProperties =
        ["Text", "Content", "Header", "Title", "PlaceholderText", "Watermark"];

    [Fact]
    public void no_markup_holds_a_sentence_of_its_own()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(SourceDirectory(), "*.axaml", SearchOption.AllDirectories))
        {
            var markup = File.ReadAllText(file);
            var name = Path.GetFileName(file);

            foreach (var property in TextProperties)
            {
                foreach (Match match in Regex.Matches(markup, $@"(?<![\w.]){property}\s*=\s*""([^""]*)"""))
                    Record(offenders, name, property, match.Groups[1].Value);
            }

            foreach (Match match in Regex.Matches(markup, @"ToolTip\.Tip\s*=\s*""([^""]*)"""))
                Record(offenders, name, "ToolTip.Tip", match.Groups[1].Value);

            foreach (Match match in Regex.Matches(markup, @"AutomationProperties\.(Name|HelpText)\s*=\s*""([^""]*)"""))
                Record(offenders, name, "AutomationProperties." + match.Groups[1].Value, match.Groups[2].Value);
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void no_code_assigns_a_sentence_to_a_property_a_reader_sees()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(SourceDirectory(), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                continue;

            var code = File.ReadAllText(file);
            var name = Path.GetFileName(file);

            // Text = "…", Content = "…", Title = "…" and their kin, including an interpolated string,
            // which is how a sentence used to be assembled here.
            foreach (var property in TextProperties)
            {
                foreach (Match match in Regex.Matches(code, $@"(?<![\w.]){property}\s*=\s*\$?""([^""]*)"""))
                    Record(offenders, name, property, match.Groups[1].Value);
            }

            foreach (Match match in Regex.Matches(code, @"AutomationProperties\.SetName\([^,]+,\s*\$?""([^""]*)"""))
                Record(offenders, name, "AutomationProperties.SetName", match.Groups[1].Value);

            foreach (Match match in Regex.Matches(code, @"ToolTip\.SetTip\([^,]+,\s*\$?""([^""]*)"""))
                Record(offenders, name, "ToolTip.SetTip", match.Groups[1].Value);
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void every_key_the_source_names_is_in_the_catalogue()
    {
        var english = Keys();
        var missing = new List<string>();

        foreach (var file in Directory.GetFiles(SourceDirectory(), "*.*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(file);
            if (extension is not (".cs" or ".axaml"))
                continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                continue;

            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);
            var patterns = new[]
            {
                @"Localized\.[A-Za-z]+\s*=\s*""([a-zA-Z][a-zA-Z.]*)""",
                @"Localized\.Set[A-Za-z]+\([^,]+,\s*""([a-zA-Z][a-zA-Z.]*)""",
                @"Message\.Of\(\s*""([a-zA-Z][a-zA-Z.]*)""",
                @"(?:Localizer|t)\.T\(\s*""([a-zA-Z][a-zA-Z.]*)""",
                @"Localizer\.T\(\s*""([a-zA-Z][a-zA-Z.]*)""",
                @"DialogButton\(\s*""([a-zA-Z][a-zA-Z.]*)""",
            };

            foreach (var pattern in patterns)
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

        Assert.Empty(missing);
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

        var unused = Keys().Where(key => !text.Contains($"\"{key}\"", StringComparison.Ordinal)).ToArray();
        Assert.Empty(unused);
    }

    private static void Record(List<string> offenders, string file, string property, string value)
    {
        var text = value.Trim();
        if (text.Length == 0 || AllowedLiterals.Contains(text))
            return;
        // A binding, a resource, a static reference or a number is not a sentence.
        if (text.StartsWith('{') || text.StartsWith('/') || !text.Any(char.IsLetter))
            return;
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
