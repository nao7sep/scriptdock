using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ScriptDock.Tests;

/// <summary>
/// The app's source as syntax, for the tests that scan it: each C# file as a syntax tree and each markup
/// file as an XML document, so a scan reads calls, assignments and attributes rather than matching text.
/// The set scanned is every source file of the app, found on disk, never a list kept by hand.
/// </summary>
internal static class SourceScan
{
    public static string AppDirectory { get; } = Path.Combine(RepoRoot(), "src", "ScriptDock");

    // The repository holding the test assembly, found by its solution file.
    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ScriptDock.slnx")))
                return directory.FullName;
        }
        throw new InvalidOperationException("The repository root was not found above the test assembly.");
    }

    public static IEnumerable<string> Files(string extension, string? directory = null) =>
        Directory.GetFiles(directory ?? AppDirectory, "*" + extension, SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    public static SyntaxNode Code(string text) => CSharpSyntaxTree.ParseText(text).GetRoot();

    public static XDocument Markup(string text) => XDocument.Parse(text);

    /// <summary>A repository file by its path below the root.</summary>
    public static string RepoFile(params string[] parts) => Path.Combine([RepoRoot(), .. parts]);

    /// <summary>A property Directory.Build.props states, as MSBuild reads it.</summary>
    public static string BuildProperty(string name) =>
        XDocument.Load(RepoFile("Directory.Build.props")).Descendants(name).Single().Value;

    /// <summary>The top-level entries of a property list, each key with the element that holds its value.</summary>
    public static IReadOnlyDictionary<string, XElement> PropertyList(string path)
    {
        var entries = XDocument.Load(path).Root!.Element("dict")!.Elements().ToList();
        var values = new Dictionary<string, XElement>(StringComparer.Ordinal);
        for (var index = 0; index + 1 < entries.Count; index += 2)
            values.Add(entries[index].Value, entries[index + 1]);
        return values;
    }

    /// <summary>The name a call is made by: <c>Of</c> in <c>Message.Of(…)</c>, <c>ShowDialog</c> in <c>x.ShowDialog(…)</c>.</summary>
    public static string? CalledName(InvocationExpressionSyntax call) => call.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        IdentifierNameSyntax name => name.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        _ => null,
    };

    /// <summary>The receiver a call is made on, by its last name: <c>Message</c> in <c>Message.Of(…)</c>.</summary>
    public static string? Receiver(InvocationExpressionSyntax call) =>
        call.Expression is MemberAccessExpressionSyntax { Expression: var receiver } ? LastName(receiver) : null;

    public static string? LastName(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax name => name.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        _ => null,
    };

    /// <summary>The text of a string literal, or of an interpolated string's fixed parts; null for anything else.</summary>
    public static string? Text(ExpressionSyntax? expression) => expression switch
    {
        LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) => literal.Token.ValueText,
        InterpolatedStringExpressionSyntax interpolated =>
            string.Concat(interpolated.Contents.OfType<InterpolatedStringTextSyntax>().Select(part => part.TextToken.ValueText)),
        _ => null,
    };

    /// <summary>The string literal a call passes at <paramref name="index"/>.</summary>
    public static string? Argument(InvocationExpressionSyntax call, int index) =>
        call.ArgumentList.Arguments.Count > index ? Text(call.ArgumentList.Arguments[index].Expression) : null;
}
