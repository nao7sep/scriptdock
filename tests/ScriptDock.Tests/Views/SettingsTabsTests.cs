using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ScriptDock.I18n;
using ScriptDock.Models;
using ScriptDock.ViewModels;
using ScriptDock.Views;
using Xunit;

namespace ScriptDock.Tests.Views;

/// <summary>
/// Settings' two tabs: a tablist in the dialog's band (composite-control conventions, Tablist) whose
/// selection swaps the body's panel, General first and the ignore patterns filling theirs.
/// </summary>
public sealed class SettingsTabsTests : WindowTest
{
    private (SettingsDialog Dialog, SettingsDialogViewModel Draft) Open(AppConfig? config = null, ThemeVariant? theme = null)
    {
        var draft = new SettingsDialogViewModel(config ?? new AppConfig());
        var dialog = new SettingsDialog(draft, _ => Task.FromResult(true));
        if (theme is not null)
            dialog.RequestedThemeVariant = theme;
        return (Show(dialog), draft);
    }

    private static void Press(TopLevel root, Key key)
    {
        root.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        root.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static Control Tab(SettingsDialog dialog, int index) => dialog.Tabs.ContainerFromIndex(index)!;

    [AvaloniaFact]
    public void General_shows_first_and_each_tab_shows_only_its_panel()
    {
        var (dialog, draft) = Open();
        var general = dialog.GetVisualDescendants().OfType<StackPanel>().Single(panel => panel.Name == "GeneralPanel");
        var patterns = dialog.GetVisualDescendants().OfType<StackPanel>().Single(panel => panel.Name == "IgnorePatternsPanel");

        Assert.Equal(0, dialog.Tabs.SelectedIndex);
        Assert.True(general.IsVisible);
        Assert.False(patterns.IsVisible);

        dialog.Tabs.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(SettingsTab.IgnorePatterns, draft.Tab);
        Assert.False(general.IsVisible);
        Assert.True(patterns.IsVisible);
        Assert.Equal(Localizer.T("settings.tabs"), Avalonia.Automation.AutomationProperties.GetName(dialog.Tabs));
    }

    [AvaloniaFact]
    public void Arrows_Home_and_End_move_the_selection_with_focus_and_do_not_wrap()
    {
        var (dialog, draft) = Open();
        Tab(dialog, 0).Focus(NavigationMethod.Tab);
        Dispatcher.UIThread.RunJobs();

        Press(dialog, Key.Left);
        Assert.Equal(SettingsTab.General, draft.Tab);
        Press(dialog, Key.Right);
        Assert.Equal(SettingsTab.IgnorePatterns, draft.Tab);
        Assert.True(Tab(dialog, 1).IsFocused);
        Press(dialog, Key.Right);
        Assert.Equal(SettingsTab.IgnorePatterns, draft.Tab);
        Press(dialog, Key.Home);
        Assert.Equal(SettingsTab.General, draft.Tab);
        Assert.True(Tab(dialog, 0).IsFocused);
        Press(dialog, Key.End);
        Assert.Equal(SettingsTab.IgnorePatterns, draft.Tab);
    }

    // The strip is one tab stop, on the selected tab, and Tab leaves it for the panel.
    [AvaloniaFact]
    public void The_strip_is_one_tab_stop_on_the_selected_tab()
    {
        var (dialog, draft) = Open();
        draft.Tab = SettingsTab.IgnorePatterns;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(KeyboardNavigationMode.Once, KeyboardNavigation.GetTabNavigation(dialog.Tabs));
        Assert.Same(Tab(dialog, 1), KeyboardNavigation.GetTabOnceActiveElement(dialog.Tabs));

        Tab(dialog, 1).Focus(NavigationMethod.Tab);
        Press(dialog, Key.Tab);
        Assert.False(Tab(dialog, 0).IsFocused);
        Assert.False(Tab(dialog, 1).IsFocused);
        Assert.Contains(dialog.FocusManager!.GetFocusedElement() as Visual,
            dialog.GetVisualDescendants().OfType<StackPanel>().Single(panel => panel.Name == "IgnorePatternsPanel").GetVisualDescendants());
    }

    [AvaloniaFact]
    public void The_patterns_tab_lists_each_built_in_with_its_note_and_Add_focuses_a_new_row()
    {
        var (dialog, draft) = Open();
        draft.Tab = SettingsTab.IgnorePatterns;
        Dispatcher.UIThread.RunJobs();
        var list = dialog.GetVisualDescendants().OfType<ItemsControl>().Single(control => control.Name == "PatternList");

        var texts = list.GetVisualDescendants().OfType<TextBlock>().Where(block => block.IsVisible).Select(block => block.Text).ToList();
        Assert.Contains(Localizer.T("ignoreNote.git"), texts);
        Assert.Contains(Localizer.T("ignoreNote.systemVolume"), texts);

        var add = dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "AddPatternButton");
        add.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(ConfigDefaults.BuiltInIgnorePatterns.Count + 1, draft.IgnorePatterns.Count);
        var field = list.ContainerFromIndex(draft.IgnorePatterns.Count - 1)!.GetVisualDescendants().OfType<global::ScriptDock.Controls.ComposingTextBox>().Single();
        Assert.True(field.IsFocused);
    }

    // The window is placed once, when shown, so a tab that grew it would push its footer off the screen: it
    // opens at General's height and keeps it on every tab (modal-dialog conventions).
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Switching_tabs_changes_neither_the_height_nor_the_position(bool fewPatterns)
    {
        var config = fewPatterns ? new AppConfig { IgnorePatterns = [new("old/")] } : new AppConfig();
        var draft = new SettingsDialogViewModel(config);
        var dialog = Track(new SettingsDialog(draft, _ => Task.FromResult(true)));
        dialog.MaxHeight = WindowMetrics.DialogMaxHeight(1400, 1);
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        var (height, position) = (dialog.Bounds.Height, dialog.Position);
        Assert.True(height < dialog.MaxHeight, "General fits this screen, so the dialog sized to it");

        foreach (var tab in new[] { SettingsTab.IgnorePatterns, SettingsTab.General, SettingsTab.IgnorePatterns })
        {
            draft.Tab = tab;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(height, dialog.Bounds.Height, 1);
            Assert.Equal(position, dialog.Position);
        }

        // The list scrolls within that height when it is longer, and leaves space below when it is short.
        var scroll = dialog.FindControl<ScrollViewer>("DialogScroll")!;
        Assert.Equal(!fewPatterns, scroll.Extent.Height > scroll.Viewport.Height);
    }

    // Each tab at a small screen's bound, in both themes, in the language with the longest words: the band
    // and footer stay in reach, the body scrolls, and nothing runs past the dialog's edges. The frames are
    // kept for a person to look at.
    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Both_tabs_fit_a_small_screen_in_German(string theme)
    {
        using var speaking = Localizer.Speaking("de");
        var (dialog, draft) = Open(theme: theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark);
        dialog.MaxHeight = WindowMetrics.DialogMaxHeight(768, 1);
        Dispatcher.UIThread.RunJobs();

        foreach (var tab in new[] { SettingsTab.General, SettingsTab.IgnorePatterns })
        {
            draft.Tab = tab;
            Dispatcher.UIThread.RunJobs();
            var scroll = dialog.FindControl<ScrollViewer>("DialogScroll")!;
            var footer = dialog.FindControl<StackPanel>("ButtonPanel")!;
            var band = dialog.FindControl<Avalonia.Controls.Presenters.ContentPresenter>("BandContent")!;

            Assert.True(dialog.Bounds.Height <= dialog.MaxHeight + 0.5);
            Assert.True(footer.TranslatePoint(new Point(0, footer.Bounds.Height), dialog)!.Value.Y <= dialog.Bounds.Height + 0.5);
            Assert.True(band.TranslatePoint(new Point(0, 0), dialog)!.Value.Y >= 0);
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height, $"{tab} should scroll at this height");
            Assert.All(dialog.GetVisualDescendants().OfType<Control>().Where(control => control.IsEffectivelyVisible && control.Bounds.Width > 0),
                control => Assert.True(control.TranslatePoint(new Point(control.Bounds.Width, 0), dialog)!.Value.X <= dialog.Bounds.Width + 0.5,
                    $"{control.GetType().Name} {control.Name} runs past the dialog"));

            using var frame = dialog.CaptureRenderedFrame();
            Assert.NotNull(frame);
            var directory = Path.Combine(SourceScan.RepoFile("tests", "ScriptDock.Tests", "TestResults", "settings-tabs"));
            Directory.CreateDirectory(directory);
            frame.Save(Path.Combine(directory, $"settings-{tab}-{theme.ToLowerInvariant()}-de.png"), PngBitmapEncoderOptions.Default);
        }
    }
}
