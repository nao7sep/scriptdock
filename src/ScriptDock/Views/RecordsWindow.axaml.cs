using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using ScriptDock.Services;
using ScriptDock.ViewModels;

namespace ScriptDock.Views;

/// <summary>
/// The Records window: <c>records.sqlite3</c> as a list beside the selected record. A durable secondary
/// window with its own placement (window-conventions, Placement) that <see cref="RecordsWindowHost"/> keeps
/// to one. Its list pane opens at the saved width and keeps the width a splitter drag ends at.
/// </summary>
public partial class RecordsWindow : Window
{
    private ScrollViewer? _listScroll;
    // The list width the user last dragged to; the shown width is derived from it on every resize.
    private double _listWidthIntent;
    private (int X, int Y, double Width, double Height)? _normalGeometry;
    private bool _closed;

    public RecordsWindow()
    {
        InitializeComponent();

        if (OperatingSystem.IsWindows())
        {
            using var iconStream = AssetLoader.Open(new Uri("avares://ScriptDock/Assets/icon-win.png"));
            Icon = new WindowIcon(iconStream);
        }

        _listWidthIntent = RecordsLayout.ListDefaultWidth;
        MinWidth = RecordsLayout.MinWindowWidth;

        // The app's inactive-window treatments (the quieter focus ring) key on this class.
        Activated += (_, _) => Classes.Set("windowInactive", false);
        Deactivated += (_, _) => Classes.Set("windowInactive", true);
        Opened += (_, _) =>
        {
            RememberNormalGeometry();
            ViewModel?.Start();
        };
        Loaded += (_, _) => RecalculateMinimums();
        PositionChanged += (_, _) => Dispatcher.UIThread.Post(RememberNormalGeometry);
        Resized += (_, _) => Dispatcher.UIThread.Post(RememberNormalGeometry);
        Shell.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
                ClampListToWindow();
        };
        ListSplitter.AddHandler(Thumb.DragCompletedEvent, OnListSplitterDragCompleted);
        RecordsList.TemplateApplied += (_, e) => AttachListScroll(e.NameScope.Find<ScrollViewer>("PART_ScrollViewer"));
        RecordsList.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _closed = true;
            ViewModel?.Dispose();
        };
    }

    private RecordsWindowViewModel? ViewModel => DataContext as RecordsWindowViewModel;

    private ColumnDefinition ListColumn => Shell.ColumnDefinitions[0];

    // The pane opens at its saved width, so the first frame already has it.
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (ViewModel is not { } vm)
            return;
        _listWidthIntent = vm.ListWidth;
        ListColumn.Width = new GridLength(_listWidthIntent, GridUnitType.Pixel);
    }

    /// <summary>The list width as the user last set it, which a narrow window may show narrower.</summary>
    internal double ListWidthIntent => _listWidthIntent;

    /// <summary>
    /// Places the window at its saved normal rectangle, and maximized on Windows when it was, before its
    /// first frame. A saved rectangle no screen can show leaves the designed size and the toolkit's own
    /// placement.
    /// </summary>
    public void RestorePlacement()
    {
        try
        {
            RecalculateMinimums();
            if (ViewModel is not { } vm
                || !Screens.All.Any(screen => WindowMetrics.CanRestoreWindowGeometry(
                    vm.WindowPositionX, vm.WindowPositionY, vm.WindowWidth, vm.WindowHeight,
                    [(screen.WorkingArea, screen.Scaling)])))
                return;

            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(vm.WindowPositionX!.Value, vm.WindowPositionY!.Value);
            Width = Math.Max(vm.WindowWidth!.Value, MinWidth);
            Height = Math.Max(vm.WindowHeight!.Value, MinHeight);
            _normalGeometry = (Position.X, Position.Y, Width, Height);
            WindowState = WindowMetrics.RestoredWindowState(vm.WindowMaximized, OperatingSystem.IsWindows());
        }
        catch (Exception ex)
        {
            // Placement is disposable: the designed size and ordinary placement stand in for it.
            Log.Warn("records window: geometry restore failed", ex);
        }
    }

    // The height floor follows the filters above the list, whose height depends on the font.
    private void RecalculateMinimums()
    {
        FiltersBand.Measure(Size.Infinity);
        MinHeight = RecordsLayout.MinWindowHeight(FiltersBand.DesiredSize.Height);
    }

    // The shell fills the window inside its margin, so its width is what the window gives the panes.
    private void ClampListToWindow() =>
        ListColumn.Width = new GridLength(
            RecordsLayout.DisplayListWidth(_listWidthIntent, Math.Max(Shell.Bounds.Width + 2 * RecordsLayout.Margin, MinWidth)),
            GridUnitType.Pixel);

    // Only a drag that ends saves the width; a resize never does (window-conventions).
    private void OnListSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        _listWidthIntent = RecordsLayout.ClampListWidth(ListColumn.ActualWidth);
        if (ViewModel is { } vm)
            _ = vm.CommitListWidthAsync(_listWidthIntent);
    }

    private void AttachListScroll(ScrollViewer? scroll)
    {
        if (_listScroll is not null)
            _listScroll.ScrollChanged -= OnListScrollChanged;
        _listScroll = scroll;
        if (scroll is not null)
            scroll.ScrollChanged += OnListScrollChanged;
    }

    // A move of the offset is the reader scrolling; a change of the extent or viewport alone is the list
    // being laid out again, after a page arrived or the window was resized.
    private void OnListScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_listScroll is not { } scroll || ViewModel is not { } vm)
            return;
        var atTop = RecordsLayout.AtTop(scroll.Offset.Y);
        var nearEnd = RecordsLayout.NearEnd(scroll.Extent.Height, scroll.Offset.Y, scroll.Viewport.Height);
        if (e.OffsetDelta.Y != 0)
            vm.ListScrolled(atTop, nearEnd);
        else
            vm.ListLaidOut(atTop, nearEnd);
    }

    // Moving on from the last row loaded reads the next page; a move that lands on it does so through the
    // selection (RecordsWindowViewModel.SelectedRecord).
    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is { } vm
            && e.KeyModifiers == KeyModifiers.None
            && e.Key is Key.Down or Key.End or Key.PageDown
            && vm.Rows.Count > 0
            && ReferenceEquals(vm.SelectedRecord, vm.Rows[^1]))
        {
            vm.LoadMore();
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        // Avalonia raises Closing again for a Close() on a window already closed.
        if (_closed)
            return;
        RememberNormalGeometry();
        if (ViewModel is { } vm
            && WindowState is WindowState.Normal or WindowState.Maximized
            && _normalGeometry is { } normal)
        {
            _ = vm.SavePlacementAsync(
                normal.X, normal.Y, normal.Width, normal.Height,
                OperatingSystem.IsWindows() && WindowState == WindowState.Maximized);
        }
    }

    private void RememberNormalGeometry()
    {
        // A position or size posted from the native events may arrive after the window closed.
        if (_closed || WindowState != WindowState.Normal)
            return;

        // Avalonia reports macOS title-bar zoom as Normal; a frame that fills the working area is not the
        // normal rectangle.
        var screen = Screens.ScreenFromWindow(this);
        if (screen is not null
            && WindowMetrics.IsMaximizedGeometry(FrameSize ?? new Size(Width, Height), screen.WorkingArea, screen.Scaling))
        {
            return;
        }

        _normalGeometry = (Position.X, Position.Y, Width, Height);
    }
}
