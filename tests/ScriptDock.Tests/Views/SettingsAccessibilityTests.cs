using System.IO;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ScriptDock.Controls;
using ScriptDock.Models;
using ScriptDock.ViewModels;
using ScriptDock.Views;
using Xunit;

namespace ScriptDock.Tests.Views;

public sealed class SettingsAccessibilityTests
{
    [AvaloniaFact]
    public void Validation_marks_the_field_and_announces_the_associated_explanation()
    {
        var vm = new SettingsDialogViewModel(new AppConfig());
        var view = new SettingsView { DataContext = vm };
        var host = new Window { Content = view, Width = 600, Height = 800 };
        host.Show();

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
        var host = new Window { Content = view, Width = 600, Height = 800 };
        host.Show();

        vm.AddExtension("bad extension");
        vm.PatternError = "A separate pattern problem.";
        Assert.True(vm.AddExtension(".command"));
        Dispatcher.UIThread.RunJobs();

        var extension = view.FindControl<ComposingTextBox>("ExtEntry")!;
        var pattern = view.FindControl<ComposingTextBox>("PatternEntry")!;
        Assert.DoesNotContain("invalid", extension.Classes);
        Assert.Contains("invalid", pattern.Classes);
        Assert.Equal(string.Empty, AutomationProperties.GetHelpText(extension));
        Assert.Equal("Invalid", AutomationProperties.GetItemStatus(pattern));
    }

    [AvaloniaFact]
    public void RootPickerResult_IsAnnouncedAndItsQuietCloseMarkMeetsTheFirstLine()
    {
        var vm = new SettingsDialogViewModel(new AppConfig());
        var view = new SettingsView { DataContext = vm };
        var host = new Window { Content = view, Width = 600, Height = 800 };
        host.Show();

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
