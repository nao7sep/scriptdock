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
using Avalonia.Platform;
using Avalonia.Threading;
using ScriptDock.Controls;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.ViewModels;

namespace ScriptDock.Views;

public partial class MainWindow : Window
{
    private readonly ConsoleInputSubmission _consoleInputSubmission = new();
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
    private double _scriptsColumnFloor;
    private double _recentColumnFloor;
    private double _headerChromeHeight = WindowMetrics.HeaderHeight;
    private double _statusChromeHeight = WindowMetrics.StatusBarHeight;
    private double _operationalErrorChromeHeight;

    public MainWindow()
    {
        InitializeComponent();

        if (OperatingSystem.IsWindows())
        {
            using var iconStream = AssetLoader.Open(new Uri("avares://ScriptDock/Assets/icon-win.png"));
            Icon = new WindowIcon(iconStream);
        }

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

            // Preserve the XAML pane floors separately from the font-dependent measured header widths.
            // Every remeasure starts from these values, so changing from a wide font back to a narrower
            // one may shrink the native minimum again instead of retaining the historical maximum.
            _scriptsColumnFloor = BodyGrid.ColumnDefinitions[0].MinWidth;
            _recentColumnFloor = BodyGrid.ColumnDefinitions[2].MinWidth;
            RecalculateMinimums();

            // Seed the user's pane INTENT from the persisted values (pixels), defaulting to the
            // XAML's own size when nothing is saved yet. The on-screen size is derived from this
            // intent, never the other way round: a window too small to honour it shows a clamped
            // display while the intent is preserved, so growing the window restores the pane.
            _recentWidthIntent = vm.SavedRecentWidth ?? BodyGrid.ColumnDefinitions[2].Width.Value;
            _consoleHeightIntent = vm.SavedConsoleHeight ?? LeftPanesGrid.RowDefinitions[2].Height.Value;

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
            vm.UiFontChanged += OnUiFontChanged;
            vm.ConfirmHandler = request =>
                ConfirmDialog.ConfirmDestructiveAsync(this, request.Title, request.Message, request.ConfirmLabel);

            await vm.InitializeAsync();
        }
        catch (Exception ex)
        {
            Log.Error("ui: window load failed", ex);
            ViewModel?.ReportShellActionError(
                "window-load",
                "ScriptDock could not finish loading this window. Check the log and try Rescan.");
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

    // Measure every font-dependent piece of fixed chrome against the CURRENT app font. The pane
    // headers drive minimum width; the actual top/status/error bars drive minimum height. This runs
    // at load and after a saved live font change or an operational-error row appears/disappears.
    private void RecalculateMinimums()
    {
        ScriptsHeader.InvalidateMeasure();
        RecentHeader.InvalidateMeasure();
        HeaderBar.InvalidateMeasure();
        StatusBar.InvalidateMeasure();
        OperationalErrorBar.InvalidateMeasure();

        ScriptsHeader.Measure(Size.Infinity);
        RecentHeader.Measure(Size.Infinity);
        HeaderBar.Measure(Size.Infinity);
        StatusBar.Measure(Size.Infinity);
        OperationalErrorBar.Measure(Size.Infinity);

        BodyGrid.ColumnDefinitions[0].MinWidth =
            Math.Max(_scriptsColumnFloor, ScriptsHeader.DesiredSize.Width);
        BodyGrid.ColumnDefinitions[2].MinWidth =
            Math.Max(_recentColumnFloor, RecentHeader.DesiredSize.Width);

        _headerChromeHeight = Math.Max(WindowMetrics.HeaderHeight, HeaderBar.DesiredSize.Height);
        _statusChromeHeight = Math.Max(WindowMetrics.StatusBarHeight, StatusBar.DesiredSize.Height);
        _operationalErrorChromeHeight = ViewModel?.HasOperationalError == true
            ? OperationalErrorBar.DesiredSize.Height
            : 0;

        MinWidth = WindowMetrics.MinWidthFor(BodyGrid.ColumnDefinitions.Select(c => c.MinWidth));
        MinHeight = WindowMetrics.MinHeightFor(
            LeftPanesGrid.RowDefinitions.Select(r => r.MinHeight),
            _headerChromeHeight,
            _statusChromeHeight,
            _operationalErrorChromeHeight);
        ClampPanesToWindow();
    }

    // DynamicResource propagation and binding visibility settle on the next dispatcher turn; measure
    // then, rather than measuring the old font or the pre-change collapsed error row.
    private void ScheduleMinimumRemeasure() => Dispatcher.UIThread.Post(RecalculateMinimums);

    private void OnUiFontChanged(object? sender, EventArgs e) => ScheduleMinimumRemeasure();

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
            var recentColumn = BodyGrid.ColumnDefinitions[2];
            var maxRecent = WindowMetrics.MaxRecentWidth(
                Width, BodyGrid.ColumnDefinitions[0].MinWidth, recentColumn.MinWidth);
            recentColumn.Width = new GridLength(
                WindowMetrics.DisplayFromIntent(recentIntent, recentColumn.MinWidth, maxRecent), GridUnitType.Pixel);
        }

        if (_consoleHeightIntent is { } consoleIntent)
        {
            var consoleRow = LeftPanesGrid.RowDefinitions[2];
            var maxConsole = WindowMetrics.MaxConsoleHeight(
                Height,
                LeftPanesGrid.RowDefinitions[0].MinHeight,
                consoleRow.MinHeight,
                _headerChromeHeight,
                _statusChromeHeight,
                _operationalErrorChromeHeight);
            consoleRow.Height = new GridLength(
                WindowMetrics.DisplayFromIntent(consoleIntent, consoleRow.MinHeight, maxConsole), GridUnitType.Pixel);
        }
    }

    // A real user drag of the Recent column splitter just finished: the resulting ActualWidth is the
    // size the user wants, so record it as the new intent. This is the ONLY place the intent changes
    // for the Recent column — the resize/clamp path never does — so a window shrink can never be
    // mistaken for the user's intent. The display already matches (the drag set it), so no re-derive.
    private void OnRecentSplitterDragCompleted(object? sender, VectorEventArgs e) =>
        _recentWidthIntent = BodyGrid.ColumnDefinitions[2].ActualWidth;

    // As above, for the console row splitter.
    private void OnConsoleSplitterDragCompleted(object? sender, VectorEventArgs e) =>
        _consoleHeightIntent = LeftPanesGrid.RowDefinitions[2].ActualHeight;

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        try
        {
            var vm = ViewModel;
            if (vm is null)
                return;

            // Quitting with Kill-on-close on terminates running work, so confirm it first: cancel this
            // close, ask, and only close for real on a yes (mirrors the dialog discard guard).
            if (!_quitConfirmed && MainWindowCloseGuard.ShouldConfirmQuit(e.CloseReason, vm.ShouldConfirmQuit()))
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
                _recentWidthIntent ?? BodyGrid.ColumnDefinitions[2].ActualWidth,
                _consoleHeightIntent ?? LeftPanesGrid.RowDefinitions[2].ActualHeight);
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
        else if (e.PropertyName == nameof(MainWindowViewModel.HasOperationalError))
        {
            ScheduleMinimumRemeasure();
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
            await SettingsDialog.EditAsync(this, draft, vm.TryApplySettings);
            vm.ResolveShellActionError("open-settings");
        }
        catch (Exception ex)
        {
            Log.Error("ui: open settings failed", ex);
            ViewModel?.ReportShellActionError("open-settings", "Settings could not be opened. Check the log and try again.");
        }
    }

    private async Task ShowShortcutsAsync()
    {
        try
        {
            await new ShortcutsDialog(_shortcuts).ShowDialog(this);
            ViewModel?.ResolveShellActionError("open-shortcuts");
        }
        catch (Exception ex)
        {
            Log.Error("ui: open shortcuts failed", ex);
            ViewModel?.ReportShellActionError("open-shortcuts", "Keyboard Shortcuts could not be opened. Check the log and try again.");
        }
    }

    private async void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await AboutDialog.ShowAsync(this);
            ViewModel?.ResolveShellActionError("open-about");
        }
        catch (Exception ex)
        {
            Log.Error("ui: open about failed", ex);
            ViewModel?.ReportShellActionError("open-about", "About ScriptDock could not be opened. Check the log and try again.");
        }
    }

    private void OnRevealLogsClick(object? sender, RoutedEventArgs e)
    {
        if (LogReveal.Reveal())
            ViewModel?.ResolveShellActionError("reveal-logs");
        else
            ViewModel?.ReportShellActionError("reveal-logs", "Logs could not be revealed. Check the console and try again.");
    }

    private void OnScriptDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: ScriptItem item })
            ViewModel?.RunScriptCommand.Execute(item);
    }

    private void OnScriptKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.None &&
            e.Key is Key.Enter or Key.Space && sender is ListBox { SelectedItem: ScriptItem item })
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
        // Only the documented unmodified Delete and Backspace gestures act on the selected entry.
        if (e.KeyModifiers == KeyModifiers.None &&
            e.Key is Key.Delete or Key.Back && sender is ListBox { SelectedItem: RecentEntry item })
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
    private async void OnConsoleInputSubmitted(object? sender, RoutedEventArgs e)
    {
        if (sender is ComposingTextBox box)
        {
            if (!_consoleInputSubmission.TryBegin(box.Text ?? string.Empty, out var snapshot))
                return; // a re-entrant Enter leaves the newly typed text untouched for the next send

            box.Text = string.Empty;
            var sent = ViewModel is { } vm && await vm.SendInputAsync(snapshot);
            box.Text = _consoleInputSubmission.Complete(snapshot, sent, box.Text ?? string.Empty);
        }
    }
}
