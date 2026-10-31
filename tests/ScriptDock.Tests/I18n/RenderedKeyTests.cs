using System;
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
using ScriptDock.I18n;
using ScriptDock.Models;
using ScriptDock.ViewModels;
using ScriptDock.Views;
using Xunit;

namespace ScriptDock.Tests.I18n;

/// <summary>
/// The rendered-message gate: no untranslated key or unresolved token reaches app-authored text.
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
        draft.ExtensionsText = "bad extension";
        draft.Validate();
        Dispatcher.UIThread.RunJobs();
        AssertNoKeys(window);
    }

    [AvaloniaFact]
    public void the_records_window_shows_no_key()
    {
        const string session = Fakes.FakeRecordReader.CurrentSession;
        var output = new RecordSummary(
            RecordKind.Run, 4, session, "2026-10-04T08:01:00.000Z", ScriptDock.Services.LogLevel.Error, "/repo/dev.command", null, null);
        var scan = new RecordSummary(RecordKind.ScanReport, 2, session, "2026-10-04T08:00:00.000Z", ScriptDock.Services.LogLevel.Info, "", "/code", 3);
        var reader = new Fakes.FakeRecordReader
        {
            Page = new RecordsPage([output, scan], false),
            Detail = (_, _) => System.Threading.Tasks.Task.FromResult<RecordDetail?>(new RunRecordDetail(
                4, session, output.Time, ScriptDock.Services.LogLevel.Error, 1, output.Title, 42, output.Time, "/runs/1.log",
                output.Time, "exited", 1, output.Time, "done\n"u8.ToArray())),
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

    [AvaloniaFact]
    public void identity_messages_render_without_arguments_in_every_language_and_after_switching()
    {
        var about = Show(new AboutDialog(_ => false));
        var main = Show(new MainWindow());
        var button = main.GetVisualDescendants().OfType<Button>().Single(control => control.Flyout is MenuFlyout);
        var menu = (MenuFlyout)button.Flyout!;
        foreach (var tag in Languages.Tags.Concat(new[] { "en" }))
        {
            using var speaking = Localizer.Speaking(tag);
            Dispatcher.UIThread.RunJobs();
            menu.ShowAt(button);
            Dispatcher.UIThread.RunJobs();
            AssertFinished(about.Title);
            Assert.Contains("ScriptDock", about.Title);
            AssertFinished(menu.Items.OfType<MenuItem>().Last().Header as string);
            Assert.Equal(about.Title, menu.Items.OfType<MenuItem>().Last().Header);

            // Modal bodies are built in the chosen language; only attached properties retranslate.
            var freshAbout = Show(new AboutDialog(_ => false));
            Assert.All(freshAbout.GetVisualDescendants().OfType<TextBlock>(), text => AssertFinished(text.Text));
            Assert.Contains(freshAbout.GetVisualDescendants().OfType<TextBlock>(), text =>
                text.Text?.StartsWith("© 2026 Yoshinao Inoguchi", StringComparison.Ordinal) == true);
            var firstRun = Show(new FirstRunFolderDialog());
            AssertFinished(firstRun.Title);
            Assert.All(firstRun.GetVisualDescendants().OfType<TextBlock>(), text => AssertFinished(text.Text));
            Assert.Contains(firstRun.GetVisualDescendants().OfType<TextBlock>(), text => text.Text?.Contains("ScriptDock") == true);

            // Native menu catalogue entries also work through the no-argument attached-property path.
            var nativeAbout = new TextBlock();
            Localized.SetText(nativeAbout, "nativeMenu.about");
            AssertFinished(nativeAbout.Text);
            Assert.Equal(about.Title, nativeAbout.Text);
            foreach (var item in MacMenuBar.Layout().SelectMany(entry => entry.Items).OfType<MacMenuBar.Item>())
                AssertFinished(item.Title);
            freshAbout.Close();
            firstRun.Close();
            menu.Hide();
        }
    }

    [AvaloniaFact]
    public void rendered_message_gate_catches_leaks_in_text_and_every_readable_attribute()
    {
        var text = new TextBlock();
        var button = new Button();
        var field = new TextBox();
        var item = new MenuItem();
        var window = Show(new Window { Content = new StackPanel { Children = { text, button, field, item } } });
        foreach (var leak in new[] { "about.title", "About {app}", "Copyright {copyright}", "Version {version}" })
        {
            foreach (var assign in new Action<string?>[] {
                value => text.Text = value,
                value => button.Content = value,
                value => item.Header = value,
                value => field.PlaceholderText = value,
                value => ToolTip.SetTip(button, value),
                value => AutomationProperties.SetName(button, value),
                value => AutomationProperties.SetHelpText(button, value),
                value => window.Title = value })
            {
                assign(leak);
                Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertNoKeys(window));
                assign(null);
            }
        }
    }

    [AvaloniaFact]
    public void rendered_dynamic_messages_preserve_literal_user_braces_and_key_collisions()
    {
        const string name = "about.title {name} {app} {version}";
        const string path = "/scripts/{count}/{copyright}/about.title.command";
        var text = new TextBlock();
        var button = new Button();
        Show(new Window { Content = new StackPanel { Children = { text, button } } });
        foreach (var tag in Languages.Tags)
        {
            using var speaking = Localizer.Speaking(tag);
            text.Text = Localizer.T("stop.message", ("name", name));
            ToolTip.SetTip(button, Localizer.T("failure.refusedNewer", ("path", path)));
            Assert.Contains(name, text.Text);
            AssertFinished(text.Text, name);
            Assert.Contains(path, ToolTip.GetTip(button) as string);
            AssertFinished(ToolTip.GetTip(button) as string, path);
            text.Text = Localizer.T("scan.addedAndRemoved",
                ("added", Message.Of("scan.added", ("count", 1))),
                ("removed", Message.Of("scan.removed", ("count", 3))));
            AssertFinished(text.Text);
            text.Text = Localizer.T("about.version", ("version", "9.8.7"));
            Assert.Contains("9.8.7", text.Text);
            AssertFinished(text.Text);
        }
    }

    // Only app-authored messages enter this assertion. Supplied names and paths stay literal, even
    // when their characters collide with catalogue keys or placeholder tokens.
    private static void AssertFinished(string? drawn, params string[] literalValues)
    {
        if (drawn is null) return;
        foreach (var literal in literalValues)
            drawn = drawn.Replace(literal, "", StringComparison.Ordinal);
        Assert.DoesNotContain(drawn.Trim(), Keys());
        Assert.DoesNotMatch(@"\{[a-zA-Z]+\}", drawn);
    }

    private static void AssertNoKeys(Visual root)
    {
        // TextBox.Text is authored user input; its placeholder remains app-authored readable text.
        // Dynamic-message fixtures check literal values separately at their rendering boundary.
        foreach (var visual in root.GetVisualDescendants().Prepend(root))
        {
            if (visual is TextBlock text) AssertFinished(text.Text);
            if (visual is not Control control) continue;
            if (control is ContentControl { Content: string content }) AssertFinished(content);
            if (control is MenuItem { Header: string header }) AssertFinished(header);
            if (control is TextBox field) AssertFinished(field.PlaceholderText);
            if (control is Window window) AssertFinished(window.Title);
            if (ToolTip.GetTip(control) is string tip) AssertFinished(tip);
            AssertFinished(AutomationProperties.GetName(control));
            AssertFinished(AutomationProperties.GetHelpText(control));
        }
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
