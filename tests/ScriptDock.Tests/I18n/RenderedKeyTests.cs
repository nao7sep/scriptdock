using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ScriptDock.Models;
using ScriptDock.ViewModels;
using ScriptDock.Views;
using Xunit;

namespace ScriptDock.Tests.I18n;

/// <summary>
/// The rendered-key gate (localization conventions): no catalogue key ever reaches the screen.
///
/// A key is a string like any other, so neither the compiler nor the source scan notices one handed
/// to a control without the translator — a whole group heading showed as <c>tasks.overdue</c> in
/// another app before this check existed. These open each surface and read what is actually drawn.
/// </summary>
public class RenderedKeyTests
{
    [AvaloniaFact]
    public void the_about_dialog_shows_no_key()
    {
        var dialog = new AboutDialog(_ => false);
        dialog.Show();
        Dispatcher.UIThread.RunJobs();

        AssertNoKeys(dialog);
    }

    [AvaloniaFact]
    public void the_shortcuts_dialog_shows_no_key()
    {
        var window = new Window();
        window.Show();
        var dialog = new ShortcutsDialog(ShortcutCatalog.Build(window));
        dialog.Show();
        Dispatcher.UIThread.RunJobs();

        AssertNoKeys(dialog);
    }

    [AvaloniaFact]
    public void the_settings_form_shows_no_key()
    {
        var draft = new SettingsDialogViewModel(new AppConfig());
        var view = new SettingsView { DataContext = draft };
        var window = new Window { Content = view, Width = 600, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        AssertNoKeys(window);

        // And with something to say: a rejected entry puts a held message on screen.
        draft.AddExtension("bad extension");
        Dispatcher.UIThread.RunJobs();
        AssertNoKeys(window);
    }

    private static void AssertNoKeys(Visual root)
    {
        var keys = Keys();
        var found = new List<string>();

        foreach (var text in root.GetVisualDescendants().OfType<TextBlock>())
        {
            if (text.Text is { } drawn && keys.Contains(drawn.Trim()))
                found.Add($"text: {drawn}");
        }

        foreach (var control in root.GetVisualDescendants().OfType<Control>())
        {
            if (control is ContentControl { Content: string content } && keys.Contains(content.Trim()))
                found.Add($"content: {content}");
            if (AutomationProperties.GetName(control) is { } name && keys.Contains(name.Trim()))
                found.Add($"automation name: {name}");
            if (AutomationProperties.GetHelpText(control) is { } help && keys.Contains(help.Trim()))
                found.Add($"help text: {help}");
        }

        if (root is Window window && window.Title is { } title && keys.Contains(title.Trim()))
            found.Add($"title: {title}");

        Assert.Empty(found);
    }

    private static HashSet<string> Keys()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(CatalogueTests.LocalesDirectory(), "en.json")));
        return document.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(System.StringComparer.Ordinal);
    }
}
