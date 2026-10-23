using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using ScriptDock.I18n;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.Tests.Fakes;
using ScriptDock.Tests.I18n;
using ScriptDock.ViewModels;
using ScriptDock.Views;
using Xunit;

namespace ScriptDock.Tests.Views;

/// <summary>
/// The Records window as the reader meets it: one window however often it is opened, its list pane at the
/// saved width from the first frame, the width saved only when a drag ends, its placement kept on close,
/// and its rows on screen in the interface language.
/// </summary>
public sealed class RecordsWindowTests : WindowTest
{
    private const string Session = FakeRecordReader.CurrentSession;

    private static readonly RecordSummary Line = new(
        RecordKind.Log, 9, Session, "2026-10-04T08:00:30.000Z", LogLevel.Warn, "run: terminate failed", null, null);
    private static readonly RecordSummary Scan = new(
        RecordKind.ScanReport, 2, Session, "2026-10-04T08:00:00.000Z", LogLevel.Info, "", """["/code"]""", 3);

    private readonly FakeRecordReader _reader = new() { Page = new RecordsPage([Line, Scan], false) };
    private readonly FakeJsonStore<AppState> _stateStore = new();

    private RecordsWindowViewModel NewViewModel() => new(_reader, _stateStore, _stateStore.Value, new FakeTimeProvider());

    private RecordsWindowHost Host() => new(NewViewModel);

    private static ListBox List(Window window) => window.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "RecordsList");

    [AvaloniaFact]
    public void Opening_it_again_brings_the_one_window_forward()
    {
        var host = Host();
        var first = Track(host.ShowOrActivate());
        Dispatcher.UIThread.RunJobs();
        first.WindowState = WindowState.Minimized;

        var second = host.ShowOrActivate();

        Assert.Same(first, second);
        Assert.Equal(WindowState.Normal, second.WindowState);
        Assert.Same(first, host.Window);
    }

    [AvaloniaFact]
    public void Closing_it_lets_the_next_open_build_a_new_one_and_stops_its_reads()
    {
        var host = Host();
        var first = host.ShowOrActivate();
        Dispatcher.UIThread.RunJobs();
        Assert.True(_reader.IsListening);

        host.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.Null(host.Window);
        Assert.False(_reader.IsListening);

        var second = Track(host.ShowOrActivate());
        Assert.NotSame(first, second);
    }

    [AvaloniaFact]
    public void Opens_with_its_list_pane_at_the_saved_width_before_the_first_frame()
    {
        _stateStore.Value.RecordsListWidth = 512;

        var window = new RecordsWindow { DataContext = NewViewModel() };
        Track(window);

        var grid = window.GetLogicalDescendants().OfType<Grid>().Single(grid => grid.Name == "Shell");
        Assert.Equal(512, grid.ColumnDefinitions[0].Width.Value);
        Assert.Equal(RecordsLayout.MinWindowWidth, window.MinWidth);
    }

    [AvaloniaFact]
    public void Saves_the_list_width_when_a_drag_ends_and_never_on_a_resize()
    {
        var window = Show(new RecordsWindow { DataContext = NewViewModel(), Width = 1200, Height = 700 });
        var grid = window.GetVisualDescendants().OfType<Grid>().Single(grid => grid.Name == "Shell");
        var splitter = window.GetVisualDescendants().OfType<GridSplitter>().Single();

        window.Width = 800;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, _stateStore.SaveCount);
        Assert.Equal(RecordsLayout.DisplayListWidth(RecordsLayout.ListDefaultWidth, 800), grid.ColumnDefinitions[0].ActualWidth);

        window.Width = 1200;
        Dispatcher.UIThread.RunJobs();
        grid.ColumnDefinitions[0].Width = new GridLength(470);
        Dispatcher.UIThread.RunJobs();
        splitter.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragCompletedEvent, Vector = new Vector(90, 0) });
        Dispatcher.UIThread.RunJobs();

        // The fake store saves on a pool thread, as the real one does.
        Assert.True(System.Threading.SpinWait.SpinUntil(() => _stateStore.SaveCount > 0, TimeSpan.FromSeconds(5)));
        Assert.Equal(470, _stateStore.Value.RecordsListWidth);
        Assert.Equal(470, window.ListWidthIntent);
    }

    [AvaloniaFact]
    public void The_list_splitter_takes_the_keyboard_and_saves_once_per_key()
    {
        var window = Show(new RecordsWindow { DataContext = NewViewModel(), Width = 1200, Height = 700 });
        var splitter = window.GetVisualDescendants().OfType<GridSplitter>().Single();

        foreach (var _ in new[] { 1, 2 }) // a held key repeats its press, then releases once
            splitter.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(RecordsLayout.ListDefaultWidth + 32, window.ListWidthIntent);
        Assert.Equal(0, _stateStore.SaveCount);

        splitter.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Right });
        Dispatcher.UIThread.RunJobs();
        // The fake store saves on a pool thread, as the real one does.
        Assert.True(System.Threading.SpinWait.SpinUntil(() => _stateStore.SaveCount > 0, TimeSpan.FromSeconds(5)));
        Assert.Equal(1, _stateStore.SaveCount);
        Assert.Equal(RecordsLayout.ListDefaultWidth + 32, _stateStore.Value.RecordsListWidth);

        // The keyboard's width is the intent, so a resize away and back keeps it.
        window.Width = 900;
        Dispatcher.UIThread.RunJobs();
        window.Width = 1200;
        Dispatcher.UIThread.RunJobs();
        var grid = window.GetVisualDescendants().OfType<Grid>().Single(grid => grid.Name == "Shell");
        Assert.Equal(RecordsLayout.ListDefaultWidth + 32, grid.ColumnDefinitions[0].ActualWidth);
    }

    [AvaloniaFact]
    public async Task Keeps_its_placement_when_it_closes()
    {
        var window = Show(new RecordsWindow { DataContext = NewViewModel(), Width = 1050, Height = 690 });

        window.Close();
        Dispatcher.UIThread.RunJobs();
        await Task.Run(() => { });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1050, _stateStore.Value.RecordsWindowWidth);
        Assert.Equal(690, _stateStore.Value.RecordsWindowHeight);
        Assert.NotNull(_stateStore.Value.RecordsWindowPositionX);
        Assert.False(_stateStore.Value.RecordsWindowMaximized);
    }

    [AvaloniaFact]
    public void Shows_the_rows_and_moving_the_selection_shows_the_selected_record()
    {
        _reader.Detail = (_, id) => Task.FromResult<RecordDetail?>(
            new LogRecordDetail(id, Session, Line.Time, LogLevel.Warn, Line.Title, """{"message":"run: terminate failed","id":3}"""));
        var window = Show(new RecordsWindow { DataContext = NewViewModel(), Width = 1100, Height = 700 });
        var list = List(window);

        Assert.Equal(2, list.ItemCount);
        list.Focus();
        list.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal((RecordKind.Log, 9L), Assert.Single(_reader.DetailReads));
        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();
        Assert.Contains(English.Of("records.scanFound", ("count", 3)), texts);
        Assert.Contains(English.Of("records.details"), texts);
    }

    [AvaloniaFact]
    public void Says_what_to_do_while_nothing_is_selected()
    {
        var window = Show(new RecordsWindow { DataContext = NewViewModel(), Width = 1100, Height = 700 });

        var note = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(text => text.Text == English.Of("records.noSelection"));
        Assert.True(note.IsEffectivelyVisible);
        Assert.Equal(English.Of("records.title"), window.Title);
    }

    [AvaloniaFact]
    public void Follows_a_language_change_while_open()
    {
        var window = Show(new RecordsWindow { DataContext = NewViewModel(), Width = 1100, Height = 700 });

        using (Localizer.Speaking("ja"))
        {
            Dispatcher.UIThread.RunJobs();
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();
            Assert.Equal(Localizer.T("records.title"), window.Title);
            Assert.Contains(Localizer.T("records.scanFound", ("count", 3)), texts);
            Assert.Contains(Localizer.T("records.levelWarn"), texts);
            Assert.DoesNotContain(English.Of("records.levelWarn"), texts);
        }
    }

    [AvaloniaFact]
    public async Task Closing_the_main_window_closes_it_first_and_saves_its_placement_with_the_view_state()
    {
        var config = new AppConfig();
        var vm = new MainWindowViewModel(
            new FakeConfigStore { Value = config }, _stateStore, new FakeJsonStore<KnownPaths>(), new FakeRecordStore(),
            config, _stateStore.Value, new KnownPaths(), new ScriptScanner(), new FakeProcessRunner());
        var host = Host();
        var main = Show(new MainWindow { DataContext = vm, Records = host });
        var records = Track(host.ShowOrActivate());
        Dispatcher.UIThread.RunJobs();

        var closed = new TaskCompletionSource();
        main.Closed += (_, _) => closed.TrySetResult();

        main.Close();
        // The main window's close saves first, then closes for real; the bound only stops a hang.
        await closed.Task.WaitAsync(System.TimeSpan.FromSeconds(30));

        Assert.False(records.IsVisible);
        Assert.Null(host.Window);
        Assert.NotNull(_stateStore.LastSaved!.RecordsWindowWidth);
    }

    [AvaloniaFact]
    public void Keeps_the_selected_row_selected_when_new_records_arrive()
    {
        var clock = new FakeTimeProvider();
        _reader.Detail = (_, id) => Task.FromResult<RecordDetail?>(
            new LogRecordDetail(id, Session, Line.Time, LogLevel.Warn, Line.Title, "{}"));
        var vm = new RecordsWindowViewModel(_reader, _stateStore, _stateStore.Value, clock);
        var window = Show(new RecordsWindow { DataContext = vm, Width = 1100, Height = 700 });
        var list = List(window);
        list.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        var newer = new RecordSummary(RecordKind.Log, 12, Session, "2026-10-04T08:02:00.000Z", LogLevel.Info, "later", null, null);
        _reader.Next(new RecordsPage([newer, Line, Scan], false));

        _reader.RaiseStored();
        Dispatcher.UIThread.RunJobs();
        clock.Advance(RecordsWindowViewModel.LiveInterval);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(3, list.ItemCount);
        Assert.Equal(2, list.SelectedIndex);
        Assert.Same(vm.Rows[2], vm.SelectedRecord);
        Assert.Single(_reader.DetailReads);
        Assert.True(vm.HasDetail);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("it")]
    [InlineData("pt-BR")]
    [InlineData("ru")]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("zh-Hans")]
    public void FullReportActionFitsAtTheMinimumWidth(string tag)
    {
        using var speaking = Localizer.Speaking(tag);
        _reader.Page = new RecordsPage([Scan], false);
        _reader.Detail = (_, _) => Task.FromResult<RecordDetail?>(new ScanReportRecordDetail(Scan.Id, Session, Scan.Time, 3, "partial", Partial: true));
        var vm = NewViewModel();
        var window = Show(new RecordsWindow { DataContext = vm, Width = RecordsLayout.MinWindowWidth, Height = 700 });
        List(window).SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();
        var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Command == vm.OpenFullReportCommand);
        Assert.True(button.IsEffectivelyVisible);
        var text = button.GetVisualDescendants().OfType<TextBlock>().First();
        Assert.True(text.Bounds.Width > 0);
        Assert.DoesNotContain(text.TextLayout.TextLines, line => line.HasCollapsed);
        Assert.True(button.Bounds.Width <= ((Control)button.Parent!).Bounds.Width);
    }

    // Rule 4 of the text-cleanup conventions: what the 1 MB view leaves out has a direct keyboard path.
    [AvaloniaFact]
    public void Open_full_output_is_reached_by_tab_and_opens_with_enter()
    {
        var run = new RecordSummary(RecordKind.Run, 4, Session, "2026-10-04T08:01:00.000Z", LogLevel.Info, "/repo/dev.command", null, null);
        _reader.Page = new RecordsPage([run], false);
        _reader.Detail = (_, _) => Task.FromResult<RecordDetail?>(new RunRecordDetail(
            4, Session, run.Time, LogLevel.Info, 3, run.Title, null, null, null, null, null, null, null,
            System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(new string('x', 79) + "\n", RecordFormat.OutputShownBytes / 80 + 1)))));
        _reader.Output = _ => Task.FromResult<byte[]?>([1, 2, 3]);
        var opened = 0;
        var vm = new RecordsWindowViewModel(_reader, _stateStore, _stateStore.Value, new FakeTimeProvider(), (_, _) =>
        {
            opened++;
            return true;
        });
        var window = Show(new RecordsWindow { DataContext = vm, Width = 1200, Height = 700 });
        var list = List(window);
        list.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();

        var button = window.GetVisualDescendants().OfType<Button>()
            .Single(button => button.Command == vm.OpenFullOutputCommand);
        Assert.True(button.IsEffectivelyVisible);
        list.Focus(NavigationMethod.Tab);
        for (var presses = 0; presses < 40 && !button.IsFocused; presses++)
            Press(window, Key.Tab);
        Assert.True(button.IsFocused);

        Press(window, Key.Enter);
        Assert.True(System.Threading.SpinWait.SpinUntil(() => { Dispatcher.UIThread.RunJobs(); return opened == 1; }, TimeSpan.FromSeconds(5)));
        Assert.Equal([4L], _reader.OutputReads);
    }

    private static void Press(TopLevel root, Key key)
    {
        root.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        root.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    // The closed filter boxes may trim a long label to fit the narrow list pane; that is an allowed
    // truncation only because opening the box (by keyboard or pointer) shows every label whole.
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("it")]
    [InlineData("pt-BR")]
    [InlineData("ru")]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("zh-Hans")]
    public void An_open_filter_shows_every_label_whole_at_the_narrowest_window(string tag)
    {
        using var speaking = Localizer.Speaking(tag);
        var window = Show(new RecordsWindow { DataContext = NewViewModel(), Width = RecordsLayout.MinWindowWidth, Height = 700 });
        var measured = 0;

        foreach (var name in new[] { "LaunchFilter", "KindFilter", "LevelFilter" })
        {
            var combo = window.GetVisualDescendants().OfType<ComboBox>().Single(combo => combo.Name == name);
            combo.IsDropDownOpen = true;
            try
            {
                Dispatcher.UIThread.RunJobs();
                for (var index = 0; index < combo.ItemCount; index++)
                {
                    var label = combo.ContainerFromIndex(index)!.GetVisualDescendants().OfType<TextBlock>().Single();
                    Assert.True(label.Bounds.Width > 0, $"{tag}: {name} item {index} was never laid out.");
                    Assert.False(
                        label.TextLayout.TextLines.Any(line => line.HasCollapsed),
                        $"{tag}: {name} shows “{label.Text}” trimmed in its open list.");
                    measured++;
                }
            }
            finally { combo.IsDropDownOpen = false; }
        }

        // Launch has All plus this launch, Kind All plus three kinds, Level All, Warnings and errors plus four levels.
        Assert.True(measured >= 12, $"{tag}: only {measured} labels were checked.");
    }

    [AvaloniaFact]
    public void Bringing_a_window_back_restores_it_from_the_dock()
    {
        var window = Show(new Window());
        window.WindowState = WindowState.Minimized;

        WindowActivation.BringBack(window);

        Assert.Equal(WindowState.Normal, window.WindowState);
        Assert.True(window.IsVisible);
    }
}
