using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ScriptDock.Controls;
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
    private bool _quitConfirmed; // set once the user confirms a kill-on-close quit, so the re-close proceeds

    // The user's INTENT for the two fixed panes, in pixels: what they last dragged the Recent column /
    // console row to. The on-screen size is DERIVED from this (clamped to what the current window can
    // fit) — so a window shrink narrows the display but never the intent, and a later grow returns the
    // pane to the intended size. Only a real splitter drag updates these; a resize never does. They
    // seed from the persisted values on load and are what gets persisted on close (never the live
    // ActualWidth, which may have been clamped down by a small window).
    private double? _recentWidthIntent;
    private double? _consoleHeightIntent;

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

            // Seed the user's pane INTENT from the persisted values (pixels), defaulting to the
            // XAML's own size when nothing is saved yet. The on-screen size is derived from this
            // intent, never the other way round: a window too small to honour it shows a clamped
            // display while the intent is preserved, so growing the window restores the pane.
            _recentWidthIntent = vm.SavedRecentWidth ?? ListsGrid.ColumnDefinitions[2].Width.Value;
            _consoleHeightIntent = vm.SavedConsoleHeight ?? BodyGrid.RowDefinitions[2].Height.Value;

            // Capture intent on a real splitter drag only. The GridSplitter's inner Thumb bubbles the
            // routed DragCompleted event up through the splitter, so hooking it here fires exactly when
            // the user finishes a drag — never on a programmatic resize/clamp. We read the resulting
            // ActualWidth/ActualHeight (the size the drag produced) and store it as the new intent.
            RecentSplitter.AddHandler(Thumb.DragCompletedEvent, OnRecentSplitterDragCompleted);
            ConsoleSplitter.AddHandler(Thumb.DragCompletedEvent, OnConsoleSplitterDragCompleted);

            // The Recent column and console row are fixed pixel sizes that don't track the window, so
            // derive their display size from the intent on load and on every resize (ClampPanesToWindow):
            // window-shrink narrows the display toward the min, window-grow returns it to the intent. The
            // bounds come from WindowMetrics — the same source the window minimum uses — so the derivation
            // and the track minimums can't disagree.
            ClampPanesToWindow();
            PropertyChanged += OnWindowPropertyChanged;

            // Catalog drives the live accelerators (the help modal renders the same source); the
            // command key (Cmd on macOS, Ctrl on Windows) is resolved by the framework.
            _shortcuts = ShortcutCatalog.Build(this);

            vm.PropertyChanged += OnViewModelPropertyChanged;
            vm.ConsoleInputFocusRequested += OnConsoleInputFocusRequested;
            vm.ConfirmHandler = request =>
                ConfirmDialog.ConfirmDestructiveAsync(this, request.Title, request.Message, request.ConfirmLabel);

            await vm.InitializeAsync();
        }
        catch (Exception ex)
        {
            Log.Error("ui: window load failed", ex);
        }
    }

    // Re-derive the fixed-size panes whenever the window resizes (ClientSize/Bounds both track it;
    // the derivation is idempotent, so reacting to either — or both — is harmless). This path reads
    // the stored intent and only updates the DISPLAY; it never changes the intent and never persists.
    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ClientSizeProperty || e.Property == BoundsProperty)
            ClampPanesToWindow();
    }

    // Set the fixed-pixel Recent column and console row to the size the current window can fit for the
    // user's stored INTENT: WindowMetrics.DisplayFromIntent(intent, min, maxFit). A fixed track doesn't
    // track the window on its own, so without this a shrink would let the console row overflow the
    // status bar and a wide Recent column would starve Scripts — and a grow would never return a pane
    // that an earlier shrink had narrowed. Bounds come from WindowMetrics, the same source as the
    // window minimum. This MUST NOT touch the intent (only a real splitter drag does) and never persists.
    private void ClampPanesToWindow()
    {
        if (_recentWidthIntent is { } recentIntent)
        {
            var recentColumn = ListsGrid.ColumnDefinitions[2];
            var maxRecent = WindowMetrics.MaxRecentWidth(
                Width, ListsGrid.ColumnDefinitions[0].MinWidth, recentColumn.MinWidth);
            recentColumn.Width = new GridLength(
                WindowMetrics.DisplayFromIntent(recentIntent, recentColumn.MinWidth, maxRecent), GridUnitType.Pixel);
        }

        if (_consoleHeightIntent is { } consoleIntent)
        {
            var consoleRow = BodyGrid.RowDefinitions[2];
            var maxConsole = WindowMetrics.MaxConsoleHeight(
                Height, BodyGrid.RowDefinitions[0].MinHeight, consoleRow.MinHeight);
            consoleRow.Height = new GridLength(
                WindowMetrics.DisplayFromIntent(consoleIntent, consoleRow.MinHeight, maxConsole), GridUnitType.Pixel);
        }
    }

    // A real user drag of the Recent column splitter just finished: the resulting ActualWidth is the
    // size the user wants, so record it as the new intent. This is the ONLY place the intent changes
    // for the Recent column — the resize/clamp path never does — so a window shrink can never be
    // mistaken for the user's intent. The display already matches (the drag set it), so no re-derive.
    private void OnRecentSplitterDragCompleted(object? sender, VectorEventArgs e) =>
        _recentWidthIntent = ListsGrid.ColumnDefinitions[2].ActualWidth;

    // As above, for the console row splitter.
    private void OnConsoleSplitterDragCompleted(object? sender, VectorEventArgs e) =>
        _consoleHeightIntent = BodyGrid.RowDefinitions[2].ActualHeight;

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        try
        {
            var vm = ViewModel;
            if (vm is null)
                return;

            // Quitting with Kill-on-close on terminates running work, so confirm it first: cancel this
            // close, ask, and only close for real on a yes (mirrors the dialog discard guard).
            if (!_quitConfirmed && vm.ShouldConfirmQuit())
            {
                e.Cancel = true;
                var proceed = await ConfirmDialog.ConfirmDestructiveAsync(
                    this,
                    "Quit ScriptDock",
                    $"{vm.RunningCount} running script(s) will be terminated when ScriptDock quits. Quit anyway?",
                    "Quit");
                if (proceed)
                {
                    _quitConfirmed = true;
                    Close();
                }
                return;
            }

            // Persist the stored INTENT, not the live ActualWidth/ActualHeight — those may have been
            // clamped down by a small window, and saving a clamped size would lose the user's intent.
            // Falls back to the live size only if no intent was ever established (defensive; OnLoaded
            // always seeds it).
            vm.PersistPaneSizes(
                _recentWidthIntent ?? ListsGrid.ColumnDefinitions[2].ActualWidth,
                _consoleHeightIntent ?? BodyGrid.RowDefinitions[2].ActualHeight);
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
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedRecentEntry))
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
        // A command accelerator is a chord the IME passes straight through, so while a field is
        // mid-composition the chord belongs to the pending candidate: stand down and let the user
        // finish, rather than firing on text the candidate is not yet part of (text-input-ime).
        if (ComposingTextBox.IsFocusedElementComposing(this))
            return;

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
            case ShortcutAction.ToggleShowHidden:
                if (ViewModel is { } vm)
                    vm.ShowHidden = !vm.ShowHidden;
                break;
            case ShortcutAction.OpenSettings:
                _ = OpenSettingsAsync();
                break;
            case ShortcutAction.ShowShortcuts:
                _ = ShowShortcutsAsync();
                break;
            case ShortcutAction.FocusScripts:
                ScriptsList.Focus();
                break;
            case ShortcutAction.FocusRecent:
                RecentList.Focus();
                break;
            case ShortcutAction.FocusConsole:
                ConsoleInput.Focus(); // no-op when disabled (no input-accepting run selected)
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
        if (sender is ListBox { SelectedItem: RecentEntry item })
            ViewModel?.RunOrRestartCommand.Execute(item);
    }

    private void OnRecentKeyDown(object? sender, KeyEventArgs e)
    {
        // Delete and Backspace (with or without Cmd/Ctrl) all remove the selected entry.
        if (e.Key is Key.Delete or Key.Back && sender is ListBox { SelectedItem: RecentEntry item })
        {
            e.Handled = true;
            if (item.IsRunning)
                ViewModel?.StopEntryCommand.Execute(item);
            else
                ViewModel?.DismissEntryCommand.Execute(item);
        }
    }

    // Send the typed line to the selected running script's stdin. Submitted is raised by
    // ComposingTextBox only on a real Enter — never the IME's candidate-commit — per the
    // text-input-ime-conventions, so a composed Enter no longer sends a half-finished line.
    private void OnConsoleInputSubmitted(object? sender, RoutedEventArgs e)
    {
        if (sender is ComposingTextBox box)
        {
            ViewModel?.SendInput(box.Text ?? string.Empty);
            box.Text = string.Empty;
        }
    }
}
