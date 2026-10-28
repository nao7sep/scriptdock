using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.Tests.Fakes;
using ScriptDock.ViewModels;
using ScriptDock.Views;
using Xunit;

namespace ScriptDock.Tests.Views;

/// <summary>
/// The list commands bound to keys reach their handlers through a focused list item. Avalonia's ListBoxItem
/// can mark a key handled before the list's own KeyDown sees it (composite-control conventions), so these
/// press the real keys on a focused item rather than raising the event on the list.
/// </summary>
public sealed class ListKeyCommandTests : WindowTest, IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scriptdock-list-keys", NanoId.New());

    void IDisposable.Dispose()
    {
        Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    [AvaloniaTheory]
    [InlineData(Key.Enter)]
    [InlineData(Key.Space)]
    public void Enter_and_space_on_a_focused_script_run_it(Key key)
    {
        Directory.CreateDirectory(_dir);
        var script = Path.Combine(_dir, "dev.command");
        File.WriteAllText(script, "echo hi\n");
        var config = new AppConfig();
        var state = new AppState();
        var runner = new FakeProcessRunner();
        var vm = new MainWindowViewModel(
            new FakeConfigStore { Value = config }, new FakeJsonStore<AppState> { Value = state }, new FakeJsonStore<KnownPaths>(),
            new FakeRecordStore(), config, state, new KnownPaths(), new ScriptScanner(), runner);
        var window = Show(new MainWindow { DataContext = vm });
        var list = window.FindControl<ListBox>("ScriptsList")!;
        list.ItemsSource = new[] { new ScriptItem(script) { DisplayName = "dev.command" } };
        Dispatcher.UIThread.RunJobs();
        list.SelectedIndex = 0;
        list.ContainerFromIndex(0)!.Focus(NavigationMethod.Tab);

        window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);

        Wait.UntilOnUi(() => runner.StartCalls.Count > 0);
        Assert.Equal(script, runner.StartCalls.Single());
    }

    [AvaloniaTheory]
    [InlineData(Key.Delete)]
    [InlineData(Key.Back)]
    public void Delete_and_backspace_on_a_focused_recent_entry_dismiss_it(Key key)
    {
        var config = new AppConfig();
        var state = new AppState();
        // The window's first load builds Recent from the records, so the entry comes from there rather than
        // being added to a list that load would rebuild.
        var records = new FakeRecordStore();
        records.Runs.Add(new RunRecord(records.Session, 1, DateTimeOffset.UtcNow, "/x/old.command", null, null, null));
        var vm = new MainWindowViewModel(
            new FakeConfigStore { Value = config }, new FakeJsonStore<AppState> { Value = state }, new FakeJsonStore<KnownPaths>(),
            records, config, state, new KnownPaths(), new ScriptScanner(), new FakeProcessRunner());
        var window = Show(new MainWindow { DataContext = vm });
        Wait.UntilOnUi(() => vm.Recent.Count == 1 && !vm.IsScanning);
        var list = window.FindControl<ListBox>("RecentList")!;
        list.SelectedIndex = 0;
        list.ContainerFromIndex(0)!.Focus(NavigationMethod.Tab);

        window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);

        Wait.UntilOnUi(() => vm.Recent.Count == 0);
        Assert.Equal(["/x/old.command"], records.Dismissals);
    }
}
