using System.IO;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ScriptDock.Controls;
using ScriptDock.Models;
using ScriptDock.ViewModels;
using ScriptDock.Views;
using ScriptDock.Tests.I18n;
using System.Linq;
using Xunit;

namespace ScriptDock.Tests.Views;

public sealed class SettingsAccessibilityTests : WindowTest
{
    [AvaloniaFact]
    public void ResetButtons_FillTheDraftWithTheBuiltIns()
    {
        var config = new AppConfig { Extensions = [".custom"], IgnorePatterns = [new("custom/")] };
        var vm = new SettingsDialogViewModel(config);
        var view = new SettingsView { DataContext = vm };
        Show(new Window { Content = view, Width = 600, Height = 800 });

        view.FindControl<Button>("ResetExtensions")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        view.FindControl<Button>("ResetIgnorePatterns")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal([ConfigDefaults.DefaultExtension], vm.Extensions.Select(row => row.Text));
        Assert.Equal(ConfigDefaults.BuiltInIgnorePatterns, vm.IgnorePatterns.Select(row => row.ToPattern()));
        Assert.True(vm.IsDirty);
    }

    [AvaloniaFact]
    public void Validation_marks_the_field_and_announces_the_associated_explanation()
    {
        var vm = new SettingsDialogViewModel(new AppConfig());
        var view = new SettingsView { DataContext = vm };
        Show(new Window { Content = view, Width = 600, Height = 800 });

        vm.AddExtension("bad extension");
        Dispatcher.UIThread.RunJobs();

        var field = view.FindControl<ComposingTextBox>("ExtEntry")!;
        var result = view.FindControl<Grid>("ExtensionErrorResult")!;
        Assert.Contains("invalid", field.Classes);
        Assert.Equal(vm.ExtensionError, AutomationProperties.GetHelpText(field));
        Assert.Equal("Invalid", AutomationProperties.GetItemStatus(field));
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(result));
        Assert.True(result.IsVisible);
    }

    [AvaloniaFact]
    public void Correcting_the_same_field_clears_invalid_state_without_touching_other_results()
    {
        var vm = new SettingsDialogViewModel(new AppConfig());
        var view = new SettingsView { DataContext = vm };
        Show(new Window { Content = view, Width = 600, Height = 800 });

        vm.AddExtension("bad extension");
        vm.Extensions[0].ErrorMessage = ScriptDock.I18n.Message.Of("settings.extensionSpaces");
        Assert.True(vm.AddExtension(".sh"));
        Dispatcher.UIThread.RunJobs();

        var extension = view.FindControl<ComposingTextBox>("ExtEntry")!;
        var row = view.FindControl<ItemsControl>("ExtList")!.GetVisualDescendants().OfType<ComposingTextBox>().First();
        Assert.DoesNotContain("invalid", extension.Classes);
        Assert.Contains("invalid", row.Classes);
        Assert.Equal(string.Empty, AutomationProperties.GetHelpText(extension));
        Assert.Equal(vm.Extensions[0].Error, AutomationProperties.GetHelpText(row));
    }

    [AvaloniaFact]
    public void RootPickerResult_IsAnnouncedAndItsQuietCloseMarkMeetsTheFirstLine()
    {
        var vm = new SettingsDialogViewModel(new AppConfig());
        var view = new SettingsView { DataContext = vm };
        Show(new Window { Content = view, Width = 600, Height = 800 });

        vm.ReportRootPickerFailure(new IOException("EACCES /private/tmp/hostile-sentinel"));
        Dispatcher.UIThread.RunJobs();

        var result = view.FindControl<Grid>("RootPickerResult")!;
        var close = view.FindControl<Button>("CloseRootPickerResult")!;
        Assert.True(result.IsVisible);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(result));
        Assert.Equal(vm.RootPickerResult, AutomationProperties.GetName(result));
        Assert.Contains("resultClose", close.Classes);
        Assert.Equal(Avalonia.Layout.VerticalAlignment.Top, close.VerticalAlignment);
        Assert.IsType<Avalonia.Controls.Shapes.Path>(close.Content);
    }

}
