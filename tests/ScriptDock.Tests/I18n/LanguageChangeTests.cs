using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
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
/// What happens when the language changes while the app is open.
///
/// This is the whole point of holding keys rather than words: a window that is already on screen
/// speaks the new language without being rebuilt, and nothing has to be restarted. A control assigned
/// once in a constructor — which is most of this app's dialogs — must follow too.
/// </summary>
public class LanguageChangeTests
{
    [AvaloniaFact]
    public void markup_that_is_already_on_screen_follows_the_language()
    {
        var view = new SettingsView { DataContext = new SettingsDialogViewModel(new AppConfig()) };
        var window = new Window { Content = view, Width = 600, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var header = Headers(window).Single(text => text.Text == English.Of("settings.theme"));

        using (Localizer.Speaking("ja"))
        {
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Localizer.T("settings.theme"), header.Text);
            Assert.NotEqual(English.Of("settings.theme"), header.Text);
        }

        // And back, so the change is a re-reading rather than a one-way overwrite.
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(English.Of("settings.theme"), header.Text);
    }

    [AvaloniaFact]
    public void a_dialog_built_in_code_follows_the_language()
    {
        var dialog = new AboutDialog(_ => false);
        dialog.Show();
        Dispatcher.UIThread.RunJobs();

        var close = dialog.GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(button.Content, English.Of("common.close")));

        using (Localizer.Speaking("ru"))
        {
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Localizer.T("common.close"), close.Content);
            Assert.Equal(Localizer.T("about.title"), dialog.Title);
        }
    }

    [AvaloniaFact]
    public void a_view_model_re_reads_what_it_holds()
    {
        var draft = new SettingsDialogViewModel(new AppConfig());
        draft.AddExtension("bad extension");

        Assert.Equal(English.Of("settings.extensionSpaces"), draft.ExtensionError);

        using (Localizer.Speaking("de"))
        {
            Assert.Equal(Localizer.T("settings.extensionSpaces"), draft.ExtensionError);
            Assert.NotEqual(English.Of("settings.extensionSpaces"), draft.ExtensionError);
        }
    }

    [AvaloniaFact]
    public void the_language_list_names_every_language_in_its_own_words()
    {
        var draft = new SettingsDialogViewModel(new AppConfig());

        // System first, then the ten languages.
        Assert.Equal(Languages.System, draft.LanguageOptions[0].Value);
        Assert.Equal(English.Of("settings.languageSystem"), draft.LanguageOptions[0].Name);
        Assert.Equal(Languages.Tags, draft.LanguageOptions.Skip(1).Select(option => option.Value));

        // Each name is the language's own, not a translation of it.
        Assert.Contains(draft.LanguageOptions, option => option.Name == "日本語");
        Assert.Contains(draft.LanguageOptions, option => option.Name == "Русский");
        Assert.Contains(draft.LanguageOptions, option => option.Name == "中文");
        Assert.Contains(draft.LanguageOptions, option => option.Name == "Português");
    }

    [AvaloniaFact]
    public void choosing_a_language_makes_the_draft_dirty()
    {
        var draft = new SettingsDialogViewModel(new AppConfig());
        Assert.False(draft.IsDirty);

        draft.Language = draft.LanguageOptions.Single(option => option.Value == "fr");

        Assert.True(draft.IsDirty);
    }

    [AvaloniaFact]
    public void a_binding_re_reads_when_a_view_model_says_every_property_changed()
    {
        // The view models answer a language change with one PropertyChanged carrying no name, which
        // means "all of them". Everything the main window shows depends on Avalonia honouring that, so
        // it is pinned here rather than assumed.
        var source = new EverythingChanged();
        var text = new TextBlock();
        text.Bind(TextBlock.TextProperty, new Binding(nameof(EverythingChanged.Words)) { Source = source });
        var window = new Window { Content = text };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("before", text.Text);

        source.Words = "after";
        source.Raise();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("after", text.Text);
    }

    private sealed class EverythingChanged : INotifyPropertyChanged
    {
        public string Words { get; set; } = "before";

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Raise() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    private static TextBlock[] Headers(Visual root) =>
        root.GetVisualDescendants().OfType<TextBlock>()
            .Where(text => text.Classes.Contains("cardHeader"))
            .ToArray();
}
