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
public class RenderedKeyTests : WindowTest
{
    [AvaloniaFact]
    public void the_about_dialog_shows_no_key()
    {
        var dialog = Show(new AboutDialog(_ => false));

        AssertNoKeys(dialog);
    }

    [AvaloniaFact]
    public void the_shortcuts_dialog_shows_no_key()
    {
        var window = Show(new Window());
        var dialog = Show(new ShortcutsDialog(ShortcutCatalog.Build(window)));

        AssertNoKeys(dialog);
    }

    [AvaloniaFact]
    public void the_settings_form_shows_no_key()
    {
        var draft = new SettingsDialogViewModel(new AppConfig());
        var view = new SettingsView { DataContext = draft };
        var window = Show(new Window { Content = view, Width = 600, Height = 900 });

        AssertNoKeys(window);

        // And with something to say: a rejected entry puts a held message on screen.
        draft.AddExtension("bad extension");
        Dispatcher.UIThread.RunJobs();
        AssertNoKeys(window);
    }

    [AvaloniaFact]
    public void the_records_window_shows_no_key()
    {
        const string session = Fakes.FakeRecordReader.CurrentSession;
        var output = new RecordSummary(
            RecordKind.RunOutput, 4, session, "2026-10-04T08:01:00.000Z", ScriptDock.Services.LogLevel.Error, "/repo/dev.command", null, null);
        var scan = new RecordSummary(RecordKind.ScanReport, 2, session, "2026-10-04T08:00:00.000Z", ScriptDock.Services.LogLevel.Info, "", "/code", 3);
        var reader = new Fakes.FakeRecordReader
        {
            Page = new RecordsPage([output, scan], false),
            Detail = (_, _) => System.Threading.Tasks.Task.FromResult<RecordDetail?>(new RunOutputRecordDetail(
                4, session, output.Time, ScriptDock.Services.LogLevel.Error, 1, output.Title, output.Time, 42, output.Time, "/runs/1.log",
                output.Time, "exited", 1, [])),
        };
        var store = new Fakes.FakeJsonStore<AppState>();
        var viewModel = new RecordsWindowViewModel(reader, store, store.Value, new Fakes.FakeTimeProvider());
        var window = Show(new RecordsWindow { DataContext = viewModel, Width = 1100, Height = 700 });

        AssertNoKeys(window);

        // And with a record selected, whose fields and blocks are put on screen.
        viewModel.SelectedRecord = viewModel.Rows[0];
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
