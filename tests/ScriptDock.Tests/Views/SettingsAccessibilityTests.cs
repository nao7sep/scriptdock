using System.IO;
using System.Runtime.CompilerServices;
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

    [Fact]
    public void Dialog_shell_bounds_dynamic_results_and_keeps_the_footer_outside_the_scroll_body()
    {
        var shell = File.ReadAllText(SourcePath("DialogBase.axaml"));
        var code = File.ReadAllText(SourcePath("DialogBase.axaml.cs"));

        Assert.Contains("SizeToContent=\"Height\"", shell);
        Assert.Contains("x:Name=\"ButtonPanel\"", shell);
        Assert.Contains("DockPanel.Dock=\"Bottom\"", shell);
        Assert.Contains("<ScrollViewer", shell);
        Assert.Contains("MaxHeight = screen.WorkingArea.Height / RenderScaling * 0.85", code);
    }

    [Fact]
    public void Dialog_action_intent_survives_keyboard_focus()
    {
        var styles = File.ReadAllText(SourcePathIn("", "App.axaml"));

        Assert.Contains("Button.accent:focus /template/ ContentPresenter", styles);
        Assert.Contains("Button.destructive /template/ ContentPresenter", styles);
        Assert.Contains("Button.destructive:focus /template/ ContentPresenter", styles);
    }

    private static string SourcePath(string file, [CallerFilePath] string caller = "") =>
        SourcePathIn("Views", file, caller);

    private static string SourcePathIn(string directory, string file, [CallerFilePath] string caller = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(caller)!, "..", "..", "..", "src", "ScriptDock", directory, file));
}
