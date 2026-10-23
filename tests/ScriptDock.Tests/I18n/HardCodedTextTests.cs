using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ScriptDock.I18n;
using Xunit;

namespace ScriptDock.Tests.I18n;

/// <summary>
/// The hard-coded-text gate (localization conventions): no sentence a reader sees is written in the
/// source, and no key reaches the source that the catalogue does not have, nor stays in the catalogue
/// when nothing names it. The source is read as syntax (<see cref="SourceScan"/>): C# as a syntax tree,
/// markup as XML. It looks only at the properties a person reads or hears — text, a button's content, a
/// menu's header, a window title, a placeholder, a tooltip and the two automation properties.
/// </summary>
public class HardCodedTextTests
{
    /// <summary>
    /// Literals allowed in a text position: the two placeholders that show an example value rather than
    /// words, a font family, a product name and a file extension, which read the same in every language. Text with no
    /// letters, such as a separator, is never a sentence and needs no entry.
    /// </summary>
    private static readonly string[] AllowedLiterals = ["Inter", ".command", "ScriptDock"];

    private static readonly string[] TextProperties =
        ["Text", "Content", "Header", "Title", "PlaceholderText", "Watermark"];

    private static readonly string[] MarkupTextAttributes =
        [.. TextProperties, "ToolTip.Tip", "AutomationProperties.Name", "AutomationProperties.HelpText"];

    // The interface-language attributes markup names keys with, such as i18n:Localized.Text.
    private static readonly XNamespace Localized = "using:ScriptDock.I18n";

    [Fact]
    public void no_markup_holds_a_sentence_of_its_own()
    {
        var offenders = new List<string>();
        foreach (var file in SourceScan.Files(".axaml"))
            MarkupOffenders(offenders, [], Path.GetFileName(file), SourceScan.Markup(File.ReadAllText(file)));
        Assert.Empty(offenders);
    }

    [Fact]
    public void no_code_assigns_a_sentence_to_a_property_a_reader_sees()
    {
        var offenders = new List<string>();
        foreach (var file in SourceScan.Files(".cs"))
            CodeOffenders(offenders, [], Path.GetFileName(file), File.ReadAllText(file));
        Assert.Empty(offenders);
    }

    // Each scan can fail: a planted sentence in each position it reads is reported.
    [Fact]
    public void the_scans_report_a_planted_sentence()
    {
        var markup = new List<string>();
        MarkupOffenders(markup, [], "planted.axaml", SourceScan.Markup(
            """<Panel xmlns="https://github.com/avaloniaui"><TextBlock Text="Save your work"/><Button ToolTip.Tip="Close the window" AutomationProperties.Name="Open the file"/></Panel>"""));
        Assert.Equal(3, markup.Count);

        var code = new List<string>();
        CodeOffenders(code, [], "planted.cs",
            """class C { void M() { Title = "Open the file"; var b = new Button { Content = "Run it now" }; AutomationProperties.SetName(b, "Name it"); ToolTip.SetTip(b, $"Stop {x} now"); } }""");
        Assert.Equal(4, code.Count);

        var missing = new List<string>();
        MissingKeys(missing, Keys(), "planted.cs", """class C { void M() { Message.Of("no.such.key"); } }""");
        Assert.Single(missing);
    }

    // An allowed literal no text position uses any more is an exception nothing needs.
    [Fact]
    public void every_allowed_literal_is_still_used()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in SourceScan.Files(".axaml"))
            MarkupOffenders([], used, Path.GetFileName(file), SourceScan.Markup(File.ReadAllText(file)));
        foreach (var file in SourceScan.Files(".cs"))
            CodeOffenders([], used, Path.GetFileName(file), File.ReadAllText(file));
        Assert.All(AllowedLiterals, literal => Assert.Contains(literal, used));
    }

    private static void MarkupOffenders(List<string> offenders, HashSet<string> allowed, string name, XDocument markup)
    {
        foreach (var attribute in markup.Descendants().Attributes())
        {
            if (attribute.Name.Namespace == XNamespace.None && MarkupTextAttributes.Contains(attribute.Name.LocalName))
                Record(offenders, allowed, name, attribute.Name.LocalName, attribute.Value);
        }
    }

    private static void CodeOffenders(List<string> offenders, HashSet<string> allowed, string name, string code)
    {
        var root = SourceScan.Code(code);

        // Text = "…", Content = "…", Title = "…" and their kin, in a statement or an object initializer,
        // including an interpolated string, which is how a sentence used to be assembled here.
        foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (SourceScan.LastName(assignment.Left) is { } property && TextProperties.Contains(property)
                && SourceScan.Text(assignment.Right) is { } value)
                Record(offenders, allowed, name, property, value);
        }

        foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var setter = (SourceScan.Receiver(call), SourceScan.CalledName(call)) switch
            {
                ("AutomationProperties", "SetName") => "AutomationProperties.SetName",
                ("ToolTip", "SetTip") => "ToolTip.SetTip",
                _ => null,
            };
            if (setter is not null && SourceScan.Argument(call, 1) is { } value)
                Record(offenders, allowed, name, setter, value);
        }
    }

    [Fact]
    public void every_key_the_source_names_is_in_the_catalogue()
    {
        var english = Keys();
        var missing = new List<string>();
        foreach (var file in SourceScan.Files(".cs"))
            MissingKeys(missing, english, Path.GetFileName(file), File.ReadAllText(file));
        foreach (var file in SourceScan.Files(".axaml"))
        {
            foreach (var key in MarkupKeys(SourceScan.Markup(File.ReadAllText(file))))
            {
                if (!english.Contains(key))
                    missing.Add($"{Path.GetFileName(file)}: {key}");
            }
        }
        Assert.Empty(missing);
    }

    // The keys code names: Message.Of("…"), Localizer.T("…") and a translator's T("…"), a dialog button's
    // label key, and Localized.SetText(control, "…") and its kin.
    private static IEnumerable<string> CodeKeys(string code)
    {
        var root = SourceScan.Code(code);
        foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var key = (SourceScan.Receiver(call), SourceScan.CalledName(call)) switch
            {
                ("Message", "Of") or (_, "T") => SourceScan.Argument(call, 0),
                ("Localized", var setter) when setter?.StartsWith("Set", StringComparison.Ordinal) == true => SourceScan.Argument(call, 1),
                _ => null,
            };
            if (key is not null)
                yield return key;
        }

        foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            if (creation.Type.ToString() == "DialogButton" && creation.ArgumentList is { Arguments.Count: > 0 } arguments
                && SourceScan.Text(arguments.Arguments[0].Expression) is { } key)
                yield return key;
        }
    }

    private static IEnumerable<string> MarkupKeys(XDocument markup) =>
        markup.Descendants().Attributes()
            .Where(attribute => attribute.Name.Namespace == Localized && attribute.Name.LocalName.StartsWith("Localized.", StringComparison.Ordinal))
            .Select(attribute => attribute.Value);

    private static void MissingKeys(List<string> missing, HashSet<string> english, string name, string code)
    {
        foreach (var key in CodeKeys(code))
        {
            if (key.Contains('.') && !english.Contains(key))
                missing.Add($"{name}: {key}");
        }
    }

    [Fact]
    public void every_key_in_the_catalogue_is_used()
    {
        // A key nothing names is dead weight every translator still has to translate. Code may build a
        // key it passes on, so every string literal in the code counts as naming it; the language names
        // are named by the language list itself.
        var named = Languages.Tags.Select(Languages.InterfaceNameKey).ToHashSet(StringComparer.Ordinal);
        foreach (var file in SourceScan.Files(".cs"))
        {
            foreach (var literal in SourceScan.Code(File.ReadAllText(file)).DescendantNodes().OfType<LiteralExpressionSyntax>())
            {
                if (SourceScan.Text(literal) is { } text)
                    named.Add(text);
            }
        }
        foreach (var file in SourceScan.Files(".axaml"))
            named.UnionWith(MarkupKeys(SourceScan.Markup(File.ReadAllText(file))));

        var unused = Keys().Where(key => !named.Contains(key)).ToList();
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
}
