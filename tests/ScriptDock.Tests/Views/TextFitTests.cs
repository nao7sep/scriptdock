using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.Tests.Fakes;
using ScriptDock.ViewModels;
using ScriptDock.Views;
using Xunit;

namespace ScriptDock.Tests.Views;

/// <summary>
/// Variable text never sizes its container (interface-styling-conventions, Labels): a name or path that
/// identifies an item takes one line and ends in one ellipsis, a name keeping its beginning and a path its
/// final segment; text to be read wraps inside an area with a height limit that scrolls, and a log's whole
/// message is in its detail.
/// </summary>
public sealed class TextFitTests : WindowTest
{
    private const string LongName =
        "a-script-whose-name-goes-on-and-on-well-past-any-chip-or-row-width-it-could-be-given.command";
    private const string LongPath =
        "/Users/someone/projects/client-work/a-very-long-folder-name/and-another-long-folder/final-folder";
    private const string LongMessage =
        "run: terminate failed for a script whose message goes on and on well past any row width it could be given";

    // What the block actually draws: its lines' runs, an ellipsis included.
    private static string Drawn(TextBlock block) =>
        string.Concat(block.TextLayout.TextLines.SelectMany(line => line.TextRuns).Select(run => run.Text.ToString()));

    private static TextBlock TextIn(Control container, string text) =>
        container.GetVisualDescendants().OfType<TextBlock>().First(block => block.Text == text);

    private static void Settle()
    {
        for (var pass = 0; pass < 3; pass++)
            Dispatcher.UIThread.RunJobs();
    }

    private static MainWindowViewModel NewMainVm(FakeRecordStore? records = null)
    {
        var config = new AppConfig();
        var state = new AppState();
        return new MainWindowViewModel(
            new FakeConfigStore { Value = config }, new FakeJsonStore<AppState> { Value = state }, new FakeJsonStore<KnownPaths>(),
            records ?? new FakeRecordStore(), config, state, new KnownPaths(), new ScriptScanner(), new FakeProcessRunner());
    }

    [AvaloniaFact]
    public void A_long_script_name_stays_on_one_line_inside_its_capped_chip()
    {
        var window = Show(new MainWindow { DataContext = NewMainVm(), Width = 1100, Height = 760 });
        var list = window.FindControl<ListBox>("ScriptsList")!;
        list.ItemsSource = new[] { new ScriptItem("/x/" + LongName) { DisplayName = LongName }, new ScriptItem("/x/b.command") { DisplayName = "b.command" } };
        Settle();
        var item = (Control)list.ContainerFromIndex(0)!;
        var chip = item.GetVisualDescendants().OfType<Border>().First(border => border.Classes.Contains("chip"));
        var name = TextIn(item, LongName);

        // At the cap, give or take the trimmed name's last glyph, and never past it.
        Assert.InRange(chip.Bounds.Width, ScriptChip.MaxWidth - 20, ScriptChip.MaxWidth);
        Assert.Single(name.TextLayout.TextLines);
        Assert.StartsWith("a-script-whose", Drawn(name));
        Assert.EndsWith("…", Drawn(name));
    }

    [AvaloniaFact]
    public async Task A_long_recent_name_takes_one_line()
    {
        // Recent is built from the records by the window's first load.
        var records = new FakeRecordStore();
        records.Runs.Add(new RunRecord(records.Session, 1, DateTimeOffset.UtcNow, "/x/" + LongName, null, null, null));
        var vm = NewMainVm(records);
        var window = Show(new MainWindow { DataContext = vm, Width = 1100, Height = 760 });
        await Wait.UntilAsync(() => vm.Recent.Count == 1 && !vm.IsScanning, vm, vm.Recent);
        Settle();
        var list = window.FindControl<ListBox>("RecentList")!;
        var row = (Control)list.ContainerFromIndex(0)!;
        var name = TextIn(row, LongName);

        Assert.Single(name.TextLayout.TextLines);
        Assert.EndsWith("…", Drawn(name));
    }

    [AvaloniaFact]
    public void The_error_bars_scroll_within_a_height_limit()
    {
        var window = Show(new MainWindow { DataContext = NewMainVm() });
        foreach (var name in new[] { "OperationalErrorText", "RecentActionErrorText" })
        {
            // The bars are hidden until a failure, so their parts are found through the logical tree.
            var area = Assert.IsType<ScrollViewer>(window.FindControl<TextBlock>(name)!.Parent);
            Assert.Contains("messageArea", area.Classes);
            Assert.True(double.IsFinite(area.MaxHeight));
        }
    }

    [AvaloniaFact]
    public void A_long_scan_folder_in_settings_keeps_its_final_segment_on_one_line()
    {
        var vm = new SettingsDialogViewModel(new AppConfig { RootDirs = [LongPath] });
        var view = new SettingsView { DataContext = vm };
        Show(new Window { Content = view, Width = 600, Height = 800 });
        Settle();
        var list = view.FindControl<ListBox>("RootList")!;
        var row = (Control)list.ContainerFromIndex(0)!;
        var path = TextIn(row, LongPath);

        Assert.Single(path.TextLayout.TextLines);
        Assert.Contains("…", Drawn(path));
        Assert.EndsWith("/final-folder", Drawn(path));
        Assert.True(row.Bounds.Width <= list.Bounds.Width);
    }

    [AvaloniaFact]
    public async Task Records_rows_and_the_detail_header_take_one_line_and_a_logs_detail_holds_its_whole_message()
    {
        var session = FakeRecordReader.CurrentSession;
        var script = LongPath + "/deploy.command";
        var run = new RecordSummary(RecordKind.Run, 4, session, "2026-10-04T08:00:00.000Z", LogLevel.Info, script, null, null);
        var log = new RecordSummary(RecordKind.Log, 9, session, "2026-10-04T08:00:30.000Z", LogLevel.Warn, LongMessage, null, null);
        var scan = new RecordSummary(RecordKind.ScanReport, 2, session, "2026-10-04T08:01:00.000Z", LogLevel.Info, "",
            $"""["{LongPath}","/b","/c"]""", 3);
        var reader = new FakeRecordReader
        {
            Page = new RecordsPage([run, log, scan], false),
            Detail = (_, id) => Task.FromResult<RecordDetail?>(new LogRecordDetail(id, session, log.Time, LogLevel.Warn, LongMessage, "{}")),
        };
        var stateStore = new FakeJsonStore<AppState>();
        var vm = new RecordsWindowViewModel(reader, stateStore, stateStore.Value, new FakeTimeProvider());
        var window = Show(new RecordsWindow { DataContext = vm, Width = 900, Height = 700 });
        Settle();
        var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "RecordsList");

        var runTitle = TextIn((Control)list.ContainerFromIndex(0)!, script);
        Assert.Single(runTitle.TextLayout.TextLines);
        Assert.EndsWith("/deploy.command", Drawn(runTitle));
        var logTitle = TextIn((Control)list.ContainerFromIndex(1)!, LongMessage);
        Assert.Single(logTitle.TextLayout.TextLines);
        Assert.StartsWith("run: terminate", Drawn(logTitle));
        var scanRow = (Control)list.ContainerFromIndex(2)!;
        Assert.Single(TextIn(scanRow, LongPath).TextLayout.TextLines);
        Assert.NotNull(TextIn(scanRow, vm.Rows[2].MoreRootsText));

        list.SelectedIndex = 1;
        await Wait.UntilAsync(() => vm.HasDetail, vm);
        Settle();
        var header = window.GetVisualDescendants().OfType<SelectableTextBlock>().First(b => b.Text == LongMessage);
        Assert.Single(header.TextLayout.TextLines);
        Assert.Contains(vm.DetailFields, field => field.Value == LongMessage);
    }

    [Fact]
    public void A_message_about_many_settings_names_a_few_and_counts_the_rest()
    {
        var message = FailurePresentation.KeptSettings("/c/config.json", ["a", "b", "c", "d", "e"]);
        var keys = Assert.IsType<ScriptDock.I18n.Message>(message.Values.Single(v => v.Name == "keys").Value);
        Assert.Equal("list.namesAndMore", keys.Key);
        Assert.Equal("a, b, c", keys.Values.Single(v => v.Name == "names").Value);
        Assert.Equal(2, keys.Values.Single(v => v.Name == "count").Value);

        var few = FailurePresentation.KeptSettings("/c/config.json", ["a", "b"]);
        Assert.Equal("a, b", few.Values.Single(v => v.Name == "keys").Value);
    }
}
