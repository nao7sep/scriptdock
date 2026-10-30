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

        Assert.Equal(ConfigDefaults.DefaultExtension, vm.ExtensionsText);
        Assert.Equal(ConfigDefaults.BuiltInIgnorePatterns, vm.IgnorePatterns.Select(row => row.ToPattern()));
        Assert.True(vm.IsDirty);
    }

    [AvaloniaFact]
    public void Validation_marks_the_field_and_announces_the_associated_explanation()
    {
        var vm = new SettingsDialogViewModel(new AppConfig());
        var view = new SettingsView { DataContext = vm };
        Show(new Window { Content = view, Width = 600, Height = 800 });

        vm.ExtensionsText = "bad extension";
        Assert.False(vm.Validate());
        Dispatcher.UIThread.RunJobs();

        var field = view.FindControl<ComposingTextBox>("ExtensionsField")!;
        var result = view.FindControl<Grid>("ExtensionErrorResult")!;
        Assert.Contains("invalid", field.Classes);
        Assert.Equal(vm.ExtensionError, AutomationProperties.GetHelpText(field));
        Assert.Equal("Invalid", AutomationProperties.GetItemStatus(field));
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(result));
        Assert.True(result.IsVisible);
    }

    [AvaloniaFact]
    public void Correcting_the_field_clears_its_invalid_state_without_touching_other_results()
    {
        var vm = new SettingsDialogViewModel(new AppConfig());
        var view = new SettingsView { DataContext = vm };
        Show(new Window { Content = view, Width = 600, Height = 800 });

        vm.ExtensionsText = "bad extension";
        var pattern = vm.AddIgnorePattern();
        pattern.Text = "//"; // a wildcard that names nothing
        Assert.False(vm.Validate());
        vm.ExtensionsText = ".sh";
        Dispatcher.UIThread.RunJobs();

        var extensions = view.FindControl<ComposingTextBox>("ExtensionsField")!;
        Assert.DoesNotContain("invalid", extensions.Classes);
        Assert.Equal(string.Empty, AutomationProperties.GetHelpText(extensions));
        Assert.True(pattern.HasError);
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
