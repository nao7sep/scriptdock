using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
        RecordKind.ScanReport, 2, Session, "2026-10-04T08:00:00.000Z", LogLevel.Info, "", "/code", 3);

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

        Assert.Equal(470, _stateStore.Value.RecordsListWidth);
        Assert.Equal(470, window.ListWidthIntent);
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
