using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.Storage;
using ScriptDock.Tests.Fakes;
using ScriptDock.Tests.Storage;
using ScriptDock.ViewModels;
using ScriptDock.Views;
using Xunit;

namespace ScriptDock.Tests.Views;

/// <summary>
/// The live console as the reader uses it: one row per line, only the rows on screen laid out, whole lines
/// copied with the command key and C, and the run's whole output opened from the console
/// (volume-handling-conventions, Output and text length).
/// </summary>
[Collection(StorageRootEnvironment.CollectionName)]
public sealed class ConsoleViewTests : WindowTest, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scriptdock-console-" + NanoId.New());
    private readonly string? _previousHome = Environment.GetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable);
    private readonly FakeProcessRunner _runner = new();

    public ConsoleViewTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, _root);
    }

    void IDisposable.Dispose()
    {
        Dispose();
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, _previousHome);
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    private static void Settle(Window window)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private MainWindowViewModel NewVm() => new(
        new FakeConfigStore(), new FakeJsonStore<AppState>(), new FakeJsonStore<KnownPaths>(), new FakeRecordStore(),
        new AppConfig(), new AppState(), new KnownPaths(), new ScriptScanner(), _runner);

    [AvaloniaFact]
    public void Only_the_lines_on_screen_are_laid_out()
    {
        var vm = NewVm();
        var window = Show(new MainWindow { DataContext = vm, Width = 1100, Height = 760 });
        vm.ApplyConsoleUpdate(new ConsoleUpdate(true, [.. Enumerable.Range(0, 3000).Select(i => $"line {i}")], ""));
        Settle(window);

        var list = window.FindControl<ListBox>("ConsoleList")!;
        Assert.Equal(3000, list.ItemCount);
        Assert.InRange(list.GetRealizedContainers().Count(), 1, 200);
    }

    [AvaloniaFact]
    public async Task The_command_key_and_C_copies_the_selected_lines_whole_and_in_order()
    {
        var vm = NewVm();
        var window = Show(new MainWindow { DataContext = vm, Width = 1100, Height = 760 });
        var long_ = new string('L', RecordFormat.OutputShownLineGraphemes * 2);
        vm.ApplyConsoleUpdate(new ConsoleUpdate(true, ["first", long_, "third", "fourth"], ""));
        Settle(window);
        var list = window.FindControl<ListBox>("ConsoleList")!;
        list.Selection.Select(2);
        list.Selection.Select(1); // selected out of order
        list.ContainerFromIndex(1)!.Focus(NavigationMethod.Pointer); // as the click that selected it does
        Settle(window);

        var command = ShortcutCatalog.CommandModifier(window) == KeyModifiers.Meta ? RawInputModifiers.Meta : RawInputModifiers.Control;
        window.KeyPress(Key.C, command, PhysicalKey.C, "c");
        window.KeyRelease(Key.C, command, PhysicalKey.C, "c");
        Settle(window);

        Assert.Equal(long_ + Environment.NewLine + "third", await window.Clipboard!.TryGetTextAsync());
    }

    [AvaloniaFact]
    public async Task Open_full_output_writes_the_runs_whole_cleaned_output_for_the_editor()
    {
        var log = Path.Combine(_root, "run.log");
        File.WriteAllText(log, "\u001b[32mready\u001b[0m\nprogress 1\rprogress 9\n");
        var process = _runner.AddRunning("/x/serve.command");
        process.LogFilePath = log;
        var vm = NewVm();
        string? opened = null;
        vm.OpenFile = path => { opened = path; return true; };
        var window = Show(new MainWindow { DataContext = vm });
        vm.Recent.Add(new RecentEntry("/x/serve.command", "serve.command", DateTimeOffset.UtcNow, process));
        vm.SelectedRecentEntry = vm.Recent.Last();
        Assert.True(System.Threading.SpinWait.SpinUntil(() => { Dispatcher.UIThread.RunJobs(); return vm.CanOpenRunOutput && vm.ConsoleLines.Count > 0; }, TimeSpan.FromSeconds(20)));
        Assert.Equal(["ready", "progress 9"], vm.ConsoleLines.Select(line => line.Text));

        await vm.OpenRunOutputCommand.ExecuteAsync(null);

        Assert.NotNull(opened);
        Assert.Equal("ready\nprogress 9\n", File.ReadAllText(opened!));
        Assert.StartsWith(Path.Combine(_root, RunOutputFile.DirectoryName), opened);
    }

    // The console follows new output while it shows the latest line, and stays where the reader scrolled
    // to once they scroll up to read.
    [AvaloniaFact]
    public void The_console_follows_new_lines_until_the_reader_scrolls_up()
    {
        var vm = NewVm();
        var window = Show(new MainWindow { DataContext = vm, Width = 1100, Height = 760 });
        vm.ApplyConsoleUpdate(new ConsoleUpdate(true, [.. Enumerable.Range(0, 500).Select(i => $"line {i}")], ""));
        Settle(window);
        var list = window.FindControl<ListBox>("ConsoleList")!;
        var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        bool AtBottom() => scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y <= 24;
        Assert.True(AtBottom());

        vm.ApplyConsoleUpdate(new ConsoleUpdate(false, [.. Enumerable.Range(500, 50).Select(i => $"line {i}")], ""));
        Settle(window);
        Assert.True(AtBottom());
        Assert.Contains(list.GetRealizedContainers(), container => container.DataContext == vm.ConsoleLines[^1]);

        scroll.Offset = new Avalonia.Vector(0, 0); // the reader scrolls to the top
        Settle(window);
        vm.ApplyConsoleUpdate(new ConsoleUpdate(false, ["line 550"], ""));
        Settle(window);
        Assert.Equal(0, scroll.Offset.Y);
    }
}
