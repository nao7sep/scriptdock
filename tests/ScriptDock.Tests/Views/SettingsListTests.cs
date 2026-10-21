using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ScriptDock.Controls;
using ScriptDock.Models;
using ScriptDock.ViewModels;
using ScriptDock.Views;
using Xunit;

namespace ScriptDock.Tests.Views;

/// <summary>The Settings lists as the user works them: rows typed in place, each removing itself, and the
/// folder list's Remove waiting for a selection, with Delete and Backspace as its keys.</summary>
public sealed class SettingsListTests : WindowTest
{
    private (SettingsView View, SettingsDialogViewModel Vm) ShowSettings(AppConfig config)
    {
        var vm = new SettingsDialogViewModel(config);
        var view = new SettingsView { DataContext = vm };
        Show(new Window { Content = view, Width = 600, Height = 900 });
        Dispatcher.UIThread.RunJobs();
        return (view, vm);
    }

    [AvaloniaFact]
    public void Each_extension_and_pattern_is_an_editable_row_that_removes_itself()
    {
        var (view, vm) = ShowSettings(new AppConfig { Extensions = [".command", ".sh"] });
        var list = view.FindControl<ItemsControl>("ExtList")!;
        var fields = list.GetVisualDescendants().OfType<ComposingTextBox>().ToList();
        Assert.Equal([".command", ".sh"], fields.Select(field => field.Text));

        fields[1].Text = ".zsh";
        Assert.Equal(".zsh", vm.Extensions[1].Text);

        var remove = list.GetVisualDescendants().OfType<Button>().First();
        remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([".zsh"], vm.Extensions.Select(row => row.Text));
    }

    [AvaloniaFact]
    public void The_folder_lists_Remove_waits_for_a_selection_and_Delete_removes_it()
    {
        var (view, vm) = ShowSettings(new AppConfig { RootDirs = ["/a", "/b"] });
        var list = view.FindControl<ListBox>("RootList")!;
        var remove = view.FindControl<Button>("RemoveRoot")!;
        Assert.False(remove.IsEnabled);

        list.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Assert.True(remove.IsEnabled);

        list.ContainerFromIndex(1)!.Focus(NavigationMethod.Tab);
        var window = TopLevel.GetTopLevel(view)!;
        window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        window.KeyRelease(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["/a"], vm.RootDirs);
    }

    [AvaloniaFact]
    public void A_failing_row_shows_its_message_on_the_row()
    {
        var (view, vm) = ShowSettings(new AppConfig());
        vm.Tab = SettingsTab.IgnorePatterns;
        var bad = vm.AddIgnorePattern();
        bad.Text = "[";
        bad.Kind = IgnorePatternKind.Regex;
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.Validate());
        Dispatcher.UIThread.RunJobs();

        var list = view.FindControl<ItemsControl>("PatternList")!;
        var field = list.GetVisualDescendants().OfType<ComposingTextBox>().Single(box => box.Text == "[");
        Assert.Contains("invalid", field.Classes);
        Assert.Contains(list.GetVisualDescendants().OfType<TextBlock>(), block => block.IsVisible && block.Text == vm.IgnorePatterns.Last().Error);
    }

    [AvaloniaFact]
    public void Save_with_a_failing_row_stays_open_and_does_not_save()
    {
        var draft = new SettingsDialogViewModel(new AppConfig());
        var bad = draft.AddIgnorePattern();
        bad.Text = "[";
        bad.Kind = IgnorePatternKind.Regex;
        var saves = 0;
        var dialog = Show(new SettingsDialog(draft, _ => { saves++; return System.Threading.Tasks.Task.FromResult(true); }));
        var save = dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Tag, "save"));

        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, saves);
        Assert.True(dialog.IsVisible);
        Assert.False(draft.HasSaveError); // the row says what is wrong, not a failed save
        Assert.True(bad.HasError);
        Assert.Equal(SettingsTab.IgnorePatterns, draft.Tab); // the failing row's tab is shown
    }
}
