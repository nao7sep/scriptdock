using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.Tests.Fakes;
using ScriptDock.ViewModels;
using Xunit;

namespace ScriptDock.Tests.Views;

public sealed class RecentActionAccessibilityTests
{
    [AvaloniaFact]
    public async Task RetainedFailureIsAssertiveOnlyWhenNew_NotWhenSelectionRestoresIt()
    {
        var config = new AppConfig();
        var state = new AppState();
        var runner = new FakeProcessRunner { TerminateResult = false };
        var vm = new MainWindowViewModel(
            new FakeJsonStore<AppConfig> { Value = config },
            new FakeJsonStore<AppState> { Value = state },
            config,
            state,
            new ScriptScanner(),
            runner);
        var process = runner.AddRunning("/x/live.command");
        var live = new RecentEntry("/x/live.command", "live.command", DateTimeOffset.UtcNow, process);
        var other = new RecentEntry("/x/other.command", "other.command", DateTimeOffset.UtcNow, process: null);
        vm.SelectedRecentEntry = live;

        var result = new Border();
        result.Bind(
            AutomationProperties.LiveSettingProperty,
            new Binding(nameof(MainWindowViewModel.RecentActionLiveSetting)) { Source = vm });
        result.Bind(
            Visual.IsVisibleProperty,
            new Binding(nameof(MainWindowViewModel.HasRecentActionError)) { Source = vm });
        var host = new Window { Content = result, Width = 400, Height = 200 };
        host.Show();

        await vm.StopEntryCommand.ExecuteAsync(live);
        Dispatcher.UIThread.RunJobs();
        Assert.True(result.IsVisible);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(result));

        vm.SelectedRecentEntry = other;
        vm.SelectedRecentEntry = live;
        Dispatcher.UIThread.RunJobs();
        Assert.True(result.IsVisible);
        Assert.Equal(AutomationLiveSetting.Off, AutomationProperties.GetLiveSetting(result));

        await vm.SendInputAsync("hello");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(result));
    }
}
