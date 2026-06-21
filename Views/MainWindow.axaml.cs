using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.ViewModels;

namespace ScriptDock.Views;

public partial class MainWindow : Window
{
    // Within this many pixels of the bottom counts as "pinned": new output then auto-scrolls.
    private const double ConsolePinThreshold = 24;

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;
    private IReadOnlyList<ShortcutItem> _shortcuts = [];
    private bool _consolePinnedToBottom = true;
    private bool _scrollConsolePending = true; // follow the console on the next layout after output/selection changes

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
        ConsoleScroll.ScrollChanged += OnConsoleScrollChanged;
        ConsoleScroll.LayoutUpdated += OnConsoleLayoutUpdated;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            var vm = ViewModel;
            if (vm is null)
                return;

            // Reserve each pane's header button row. The right-aligned actions (Run/Hide/Show-hidden/
            // Rescan on Scripts; Run/Stop/Dismiss on Recent) overlap the section title once the column
            // is narrower than the header needs, so measure each header's natural width and raise the
            // column minimum to fit it. The width is font-dependent, so it's measured, not guessed.
            ScriptsHeader.Measure(Size.Infinity);
            RecentHeader.Measure(Size.Infinity);
            ListsGrid.ColumnDefinitions[0].MinWidth =
                Math.Max(ListsGrid.ColumnDefinitions[0].MinWidth, ScriptsHeader.DesiredSize.Width);
            ListsGrid.ColumnDefinitions[2].MinWidth =
                Math.Max(ListsGrid.ColumnDefinitions[2].MinWidth, RecentHeader.DesiredSize.Width);

            // Derive the window minimum from the live grids plus fixed chrome (see WindowMetrics)
            // rather than a hand-typed constant, so the window can never be shrunk small enough to
            // hide a pane or overlap the status bar — and so changing a column/row minimum (or the
            // measured header width above) moves the window minimum with it. The status bar sits in
            // its own reserved track, so the body fill can't cover it.
            MinWidth = WindowMetrics.MinWidthFor(
                ListsGrid.ColumnDefinitions.Select(c => c.MinWidth));
            MinHeight = WindowMetrics.MinHeightFor(
                BodyGrid.RowDefinitions.Select(r => r.MinHeight));

            // Restore persisted pane sizes, clamped to the current window so a size saved on a larger
            // screen can't squeeze a pane to nothing here. The bounds come from WindowMetrics — the
            // same source the window minimum uses — so the clamp and the track minimums can't disagree.
            var recentColumnMin = ListsGrid.ColumnDefinitions[2].MinWidth;
            var scriptsColumnMin = ListsGrid.ColumnDefinitions[0].MinWidth;
            var consoleRowMin = BodyGrid.RowDefinitions[2].MinHeight;
            var listsRowMin = BodyGrid.RowDefinitions[0].MinHeight;

            if (vm.SavedRecentWidth is { } recentWidth)
            {
                var maxRecent = WindowMetrics.MaxRecentWidth(Width, scriptsColumnMin, recentColumnMin);
                ListsGrid.ColumnDefinitions[2].Width =
                    new GridLength(Math.Clamp(recentWidth, recentColumnMin, maxRecent), GridUnitType.Pixel);
            }
            if (vm.SavedConsoleHeight is { } consoleHeight)
            {
                var maxConsole = WindowMetrics.MaxConsoleHeight(Height, listsRowMin, consoleRowMin);
                BodyGrid.RowDefinitions[2].Height =
                    new GridLength(Math.Clamp(consoleHeight, consoleRowMin, maxConsole), GridUnitType.Pixel);
            }

            // The Recent column and console row are fixed pixel sizes that don't shrink when the
            // window does, so re-clamp them on every resize — otherwise shrinking the window lets the
            // console spill over the status bar, and a wide Recent column starves the Scripts pane.
            ClampPanesToWindow();
            PropertyChanged += OnWindowPropertyChanged;

            // Catalog drives the live accelerators (the help modal renders the same source); the
            // command key (Cmd on macOS, Ctrl on Windows) is resolved by the framework.
            _shortcuts = ShortcutCatalog.Build(this);

            vm.PropertyChanged += OnViewModelPropertyChanged;
            vm.ConsoleInputFocusRequested += OnConsoleInputFocusRequested;

            await vm.InitializeAsync();
        }
        catch (Exception ex)
        {
            Log.Error("ui: window load failed", ex);
        }
    }

    // Re-clamp the fixed-size panes whenever the window resizes (ClientSize/Bounds both track it;
    // the clamp is idempotent, so reacting to either — or both — is harmless).
    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ClientSizeProperty || e.Property == BoundsProperty)
            ClampPanesToWindow();
    }

    // Keep the fixed-pixel Recent column and console row within the current window. A fixed track
    // doesn't shrink when the window does, so without this the console row would overflow past the
    // window edge over the status bar, and a wide Recent column would push Scripts below its minimum.
    // Bounds come from WindowMetrics — the same source as the window minimum and the restore clamp.
    private void ClampPanesToWindow()
    {
        var recentColumn = ListsGrid.ColumnDefinitions[2];
        var maxRecent = WindowMetrics.MaxRecentWidth(
            Width, ListsGrid.ColumnDefinitions[0].MinWidth, recentColumn.MinWidth);
        if (recentColumn.Width.IsAbsolute && recentColumn.Width.Value > maxRecent)
            recentColumn.Width = new GridLength(maxRecent, GridUnitType.Pixel);

        var consoleRow = BodyGrid.RowDefinitions[2];
        var maxConsole = WindowMetrics.MaxConsoleHeight(
            Height, BodyGrid.RowDefinitions[0].MinHeight, consoleRow.MinHeight);
        if (consoleRow.Height.IsAbsolute && consoleRow.Height.Value > maxConsole)
            consoleRow.Height = new GridLength(maxConsole, GridUnitType.Pixel);
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        try
        {
            var vm = ViewModel;
            if (vm is null)
                return;

            vm.PersistPaneSizes(ListsGrid.ColumnDefinitions[2].ActualWidth, BodyGrid.RowDefinitions[2].ActualHeight);
            vm.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Error("ui: window close failed", ex);
        }
    }

    // Console: keep the view glued to the latest output unless the user has scrolled up to read.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedDockEntry))
        {
            _consolePinnedToBottom = true; // a freshly-selected run starts pinned to its latest line
            _scrollConsolePending = true;
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.SelectedOutput) && _consolePinnedToBottom)
        {
            _scrollConsolePending = true; // follow new output — but only after it has been laid out
        }
    }

    // The view-model asks for this when an input-accepting run starts. Post it so the input's
    // IsEnabled (bound to CanSendInput) has settled first — focusing a disabled control is a no-op.
    private void OnConsoleInputFocusRequested(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => ConsoleInput.Focus());

    private void OnConsoleScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // Only a genuine user scroll flips the pinned state — an offset change while the extent is
        // unchanged. Content growth (ExtentDelta != 0) is new output arriving, not the user, and
        // following it is handled in OnConsoleLayoutUpdated — so growth must never unpin.
        if (e.ExtentDelta.Y != 0)
            return;

        var distanceFromBottom = ConsoleScroll.Extent.Height - ConsoleScroll.Viewport.Height - ConsoleScroll.Offset.Y;
        _consolePinnedToBottom = distanceFromBottom <= ConsolePinThreshold;
    }

    // Runs after each layout pass, so the console's Extent reflects the latest output by now. A
    // pending follow then scrolls to the true bottom — fixing the race where the scroll ran before
    // the new content had grown the extent, leaving the view stuck at the top (notably while the log
    // was still shorter than the field).
    private void OnConsoleLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_scrollConsolePending || !_consolePinnedToBottom)
            return;

        _scrollConsolePending = false;
        var maxY = Math.Max(0, ConsoleScroll.Extent.Height - ConsoleScroll.Viewport.Height);
        ConsoleScroll.Offset = new Vector(ConsoleScroll.Offset.X, maxY);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        foreach (var item in _shortcuts)
        {
            if (item.Gesture is { } gesture && gesture.Matches(e))
            {
                e.Handled = true;
                TryRunShortcut(item.Action!.Value);
                return;
            }
        }
    }

    private void TryRunShortcut(ShortcutAction action)
    {
        switch (action)
        {
            case ShortcutAction.Rescan:
                if (ViewModel?.RescanCommand.CanExecute(null) == true)
                    ViewModel.RescanCommand.Execute(null);
                break;
            case ShortcutAction.OpenSettings:
                _ = OpenSettingsAsync();
                break;
            case ShortcutAction.ShowShortcuts:
                _ = ShowShortcutsAsync();
                break;
        }
    }

    private void OnSettingsClick(object? sender, RoutedEventArgs e) => _ = OpenSettingsAsync();

    private void OnShortcutsClick(object? sender, RoutedEventArgs e) => _ = ShowShortcutsAsync();

    private async Task OpenSettingsAsync()
    {
        try
        {
            var vm = ViewModel;
            if (vm is null)
                return;

            var draft = vm.CreateSettingsDraft();
            if (await SettingsDialog.EditAsync(this, draft))
                vm.ApplySettings(draft);
        }
        catch (Exception ex)
        {
            Log.Error("ui: open settings failed", ex);
        }
    }

    private async Task ShowShortcutsAsync()
    {
        try
        {
            await new ShortcutsDialog(_shortcuts).ShowDialog(this);
        }
        catch (Exception ex)
        {
            Log.Error("ui: open shortcuts failed", ex);
        }
    }

    private async void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        try { await AboutDialog.ShowAsync(this); }
        catch (Exception ex) { Log.Error("ui: open about failed", ex); }
    }

    private void OnRevealLogsClick(object? sender, RoutedEventArgs e)
    {
        try { LogReveal.Reveal(); }
        catch (Exception ex) { Log.Error("ui: reveal logs failed", ex); }
    }

    private void OnScriptDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: ScriptItem item })
            ViewModel?.RunScriptCommand.Execute(item);
    }

    private void OnScriptKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space && sender is ListBox { SelectedItem: ScriptItem item })
        {
            e.Handled = true;
            ViewModel?.RunScriptCommand.Execute(item);
        }
    }

    private void OnRecentDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: DockEntry item })
            ViewModel?.RunOrRestartCommand.Execute(item);
    }

    private void OnRecentKeyDown(object? sender, KeyEventArgs e)
    {
        // Delete and Backspace (with or without Cmd/Ctrl) all remove the selected entry.
        if (e.Key is Key.Delete or Key.Back && sender is ListBox { SelectedItem: DockEntry item })
        {
            e.Handled = true;
            if (item.IsRunning)
                ViewModel?.StopEntryCommand.Execute(item);
            else
                ViewModel?.DismissEntryCommand.Execute(item);
        }
    }

    // Send the typed line to the selected running script's stdin on Enter. IME guard: only a real
    // Enter (never the IME's Key.ImeProcessed) sends, per the text-input-ime-conventions.
    private void OnConsoleInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        if (sender is TextBox box)
        {
            ViewModel?.SendInput(box.Text ?? string.Empty);
            box.Text = string.Empty;
        }
    }
}
