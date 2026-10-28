using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ScriptDock.Controls;
using ScriptDock.Models;
using ScriptDock.I18n;
using ScriptDock.Services;
using ScriptDock.Storage;
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
    private ScrollViewer? _consoleScroll; // the console list's own scroller

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
    private (int X, int Y, double Width, double Height)? _normalGeometry;

    public MainWindow()
    {
        InitializeComponent();
        CloseWindowShortcut.Attach(this);

        // A focused ListBoxItem marks Enter and Space handled for its own selection before the list's KeyDown
        // sees them (composite-control conventions), so the run gesture listens to handled events too.
        ScriptsList.AddHandler(KeyDownEvent, OnScriptKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);

        _scriptsColumnFloor = BodyGrid.ColumnDefinitions[0].MinWidth;
        _recentColumnFloor = BodyGrid.ColumnDefinitions[2].MinWidth;
        // The minimum follows the chrome as each layout pass leaves it (RecalculateMinimums).
        LayoutUpdated += (_, _) => RecalculateMinimums();
        if (OperatingSystem.IsWindows())
        {
            using var iconStream = AssetLoader.Open(new Uri("avares://ScriptDock/Assets/icon-win.png"));
            Icon = new WindowIcon(iconStream);
        }

        // The app's inactive-window treatments (the quieter focus ring) key on this class, as in DialogBase.
        Activated += (_, _) =>
        {
            Classes.Set("windowInactive", false);
            ViewModel?.OnWindowActivated(); // the Scripts pane rescans when the window comes back to the front
        };
        Deactivated += (_, _) => Classes.Set("windowInactive", true);
        Loaded += OnLoaded;
        LayoutRoot.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
                ClampPanesToWindow();
        };
        PositionChanged += (_, _) =>
        {
            RememberNormalGeometryAfterNativeEvents();
            ApplyNativeMinimum();
        };
        Resized += (_, _) => RememberNormalGeometryAfterNativeEvents();
        Opened += (_, _) => RememberNormalGeometry();
        ScalingChanged += (_, _) => ApplyNativeMinimum();
        Screens.Changed += OnScreensChanged;
        Closed += (_, _) => Screens.Changed -= OnScreensChanged;
        Closing += OnClosing;
        ConsoleList.TemplateApplied += (_, e) =>
        {
            if (_consoleScroll is not null)
                _consoleScroll.ScrollChanged -= OnConsoleScrollChanged;
            _consoleScroll = e.NameScope.Find<ScrollViewer>("PART_ScrollViewer");
            if (_consoleScroll is not null)
                _consoleScroll.ScrollChanged += OnConsoleScrollChanged;
        };
        ConsoleList.LayoutUpdated += OnConsoleLayoutUpdated;
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
            // The pane sizes a key sets are intents like a drag's, saved with the view state at quit.
            SplitterKeyboard.Attach(RecentSplitter, Avalonia.Layout.Orientation.Horizontal, paneBefore: false,
                () => (BodyGrid.ColumnDefinitions[2].Width.Value, BodyGrid.ColumnDefinitions[2].MinWidth, MaxRecentWidth()),
                width =>
                {
                    _recentWidthIntent = width;
                    ClampPanesToWindow();
                });
            SplitterKeyboard.Attach(ConsoleSplitter, Avalonia.Layout.Orientation.Vertical, paneBefore: false,
                () => (LeftPanesGrid.RowDefinitions[2].Height.Value, LeftPanesGrid.RowDefinitions[2].MinHeight, MaxConsoleHeight()),
                height =>
                {
                    _consoleHeightIntent = height;
                    ClampPanesToWindow();
                });

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
            vm.ConsoleLines.CollectionChanged += OnConsoleLinesChanged;
            vm.ConsoleInputFocusRequested += OnConsoleInputFocusRequested;
            vm.IsDialogOpen = () => OwnedWindows.OfType<DialogBase>().Any();
            vm.ConfirmHandler = request =>
                ConfirmDialog.ConfirmDestructiveAsync(this, request.Title, request.Message, request.ConfirmLabelKey);

            await vm.InitializeAsync();
        }
        catch (Exception ex)
        {
            Log.Error("ui: window load failed", ex);
            ViewModel?.ReportShellActionError(
                "window-load",
                Message.Of("shell.windowLoadFailed"));
        }
    }

    // Re-derive the fixed-size panes whenever the window resizes (ClientSize/Bounds both track it;
    // the derivation is idempotent, so reacting to either — or both — is harmless). This path reads
    // the stored intent and only updates the DISPLAY; it never changes the intent and never persists.
    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ClientSizeProperty || e.Property == BoundsProperty)
        {
            ClampPanesToWindow();
        }
    }

    public void RestoreWindowGeometry()
    {
        if (ViewModel is not { } vm)
            return;

        try
        {
            var target = Screens.All.FirstOrDefault(screen => WindowMetrics.CanRestoreWindowGeometry(
                vm.WindowPositionX, vm.WindowPositionY, vm.WindowWidth, vm.WindowHeight,
                [(screen.WorkingArea, screen.Scaling)]));
            if (target is null)
                return;

            RecalculateMinimums();
            ApplyNativeMinimum(target);
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(vm.WindowPositionX!.Value, vm.WindowPositionY!.Value);
            Width = Math.Max(vm.WindowWidth!.Value, MinWidth);
            Height = Math.Max(vm.WindowHeight!.Value, MinHeight);
            _normalGeometry = (Position.X, Position.Y, Width, Height);
            WindowState = WindowMetrics.RestoredWindowState(
                vm.WindowMaximized, OperatingSystem.IsWindows());
        }
        catch (Exception ex)
        {
            // Placement is disposable. Keep the designed defaults if the display
            // backend or saved values cannot be used.
            Log.Warn("window geometry restore failed", ex);
        }
    }

    private void OnScreensChanged(object? sender, EventArgs e) => ApplyNativeMinimum();

    private void ApplyNativeMinimum(Screen? target = null)
    {
        // The window's minimum is the layout's own floor, uncapped, with no window-level scroll region
        // (window-conventions).
        MinWidth = LayoutRoot.MinWidth;
        MinHeight = LayoutRoot.MinHeight;
    }

    // The window's minimum follows its chrome as each layout pass leaves it, so a new language, label or
    // UI font moves it with no measuring of its own: the pane headers drive the minimum width, and the top,
    // status and error bars the minimum height. Measuring the live chrome here instead (Measure at an
    // unlimited size, outside the pass) left Avalonia's cached layout stale on the Mac after a language
    // change, keeping German button slots in Japanese until a resize (window-conventions, Content-based
    // minimum size). Applied only when a value changes, so the pass it starts settles at once.
    private void RecalculateMinimums()
    {
        var scriptsMin = Math.Max(_scriptsColumnFloor, WindowMetrics.HeaderWidth(ScriptsHeader, ScriptsTitle, ScriptsActions));
        var recentMin = Math.Max(_recentColumnFloor, WindowMetrics.HeaderWidth(RecentHeader, RecentTitle, RecentActions));
        var headerHeight = Math.Max(WindowMetrics.HeaderHeight, HeaderBar.DesiredSize.Height);
        var statusHeight = Math.Max(WindowMetrics.StatusBarHeight, StatusBar.DesiredSize.Height);
        var errorHeight = OperationalErrorBar.IsVisible ? OperationalErrorBar.DesiredSize.Height : 0;
        if (scriptsMin == BodyGrid.ColumnDefinitions[0].MinWidth
            && recentMin == BodyGrid.ColumnDefinitions[2].MinWidth
            && headerHeight == _headerChromeHeight
            && statusHeight == _statusChromeHeight
            && errorHeight == _operationalErrorChromeHeight)
            return;

        BodyGrid.ColumnDefinitions[0].MinWidth = scriptsMin;
        BodyGrid.ColumnDefinitions[2].MinWidth = recentMin;
        _headerChromeHeight = headerHeight;
        _statusChromeHeight = statusHeight;
        _operationalErrorChromeHeight = errorHeight;

        LayoutRoot.MinWidth = WindowMetrics.MinWidthFor(BodyGrid.ColumnDefinitions.Select(c => c.MinWidth));
        LayoutRoot.MinHeight = WindowMetrics.MinHeightFor(
            LeftPanesGrid.RowDefinitions.Select(r => r.MinHeight),
            _headerChromeHeight,
            _statusChromeHeight,
            _operationalErrorChromeHeight);
        ApplyNativeMinimum();
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
            var recentColumn = BodyGrid.ColumnDefinitions[2];
            recentColumn.Width = new GridLength(
                WindowMetrics.DisplayFromIntent(recentIntent, recentColumn.MinWidth, MaxRecentWidth()), GridUnitType.Pixel);
        }

        if (_consoleHeightIntent is { } consoleIntent)
        {
            var consoleRow = LeftPanesGrid.RowDefinitions[2];
            consoleRow.Height = new GridLength(
                WindowMetrics.DisplayFromIntent(consoleIntent, consoleRow.MinHeight, MaxConsoleHeight()), GridUnitType.Pixel);
        }
    }

    private double MaxRecentWidth() =>
        WindowMetrics.MaxRecentWidth(
            Math.Max(LayoutRoot.Bounds.Width, LayoutRoot.MinWidth),
            BodyGrid.ColumnDefinitions[0].MinWidth,
            BodyGrid.ColumnDefinitions[2].MinWidth);

    private double MaxConsoleHeight() =>
        WindowMetrics.MaxConsoleHeight(
            Math.Max(LayoutRoot.Bounds.Height, LayoutRoot.MinHeight),
            LeftPanesGrid.RowDefinitions[0].MinHeight,
            LeftPanesGrid.RowDefinitions[2].MinHeight,
            _headerChromeHeight,
            _statusChromeHeight,
            _operationalErrorChromeHeight);

    // A real user drag of the Recent column splitter just finished: the resulting ActualWidth is the
    // size the user wants, so record it as the new intent. This is the ONLY place the intent changes
    // for the Recent column — the resize/clamp path never does — so a window shrink can never be
    // mistaken for the user's intent. The display already matches (the drag set it), so no re-derive.
    private void OnRecentSplitterDragCompleted(object? sender, VectorEventArgs e) =>
        _recentWidthIntent = BodyGrid.ColumnDefinitions[2].ActualWidth;

    // As above, for the console row splitter.
    private void OnConsoleSplitterDragCompleted(object? sender, VectorEventArgs e) =>
        _consoleHeightIntent = LeftPanesGrid.RowDefinitions[2].ActualHeight;

    // The quit's work, started once by whichever quit reaches it first. Once it has ended, the next close
    // passes, whatever it did, so a failure in it never holds the window open.
    private Task? _quitWork;

    // The quit the user started, while it asks or runs; a second close request joins it by doing nothing.
    private Task<bool>? _quit;

    // The operating system is ending the session and has taken the quit over.
    private bool _sessionEnding;

    // The quit's question on screen, which a session end closes.
    private DialogBase? _quitQuestion;

    /// <summary>
    /// First-run setup (config-sets-conventions): with no folder to scan, ask for one in a single step and
    /// scan it. Not Now writes nothing, so the question returns at the next launch while there is still no
    /// folder; Settings can add folders at any time. Nothing more is shown once a quit has begun.
    /// </summary>
    internal async Task AskForFirstScanFolderAsync()
    {
        if (ViewModel is not { NeedsScanFolder: true } vm || vm.Quitting.IsCancellationRequested
            || !await FirstRunFolderDialog.AskAsync(this) || vm.Quitting.IsCancellationRequested)
            return;

        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = Localizer.T("settings.pickRootTitle"),
            });
            if (vm.Quitting.IsCancellationRequested || folders.Count == 0 || folders[0].TryGetLocalPath() is not { } path)
                return;
            if (!await vm.AddFirstScanFolderAsync(path) && !vm.Quitting.IsCancellationRequested)
                await NoticeDialog.ShowAsync(this, Message.Of("firstRun.title"), Message.Of("settings.saveFailed"));
        }
        catch (Exception ex)
        {
            Log.Error("ui: first-run folder choice failed", ex);
            if (!vm.Quitting.IsCancellationRequested)
                await NoticeDialog.ShowAsync(this, Message.Of("firstRun.title"), FailurePresentation.RootPicker(ex));
        }
    }

    // Every quit path closes the main window: the menu's Quit and Cmd+Q, the Dock's Quit through the
    // lifetime, the window's own close, and the operating system ending the session
    // (unsaved-edits-conventions, Quitting).
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        var vm = ViewModel;
        if (vm is null || _quitWork is { IsCompleted: true })
            return;

        if (SessionEnd.Is(e.CloseReason))
        {
            // The system waits for this close to answer, so the quit's work runs here to its deadline, and
            // the window closes whatever it did. A question still up closes, and a quit the user started
            // ends with this one.
            _sessionEnding = true;
            _quitQuestion?.Close();
            RunUntilDone(EndSessionAsync(vm));
            return;
        }

        // Closing does not itself wait for an async handler, so without this the window (and, as the last
        // window, the app) would finish closing while the quit's work is still in flight. Cancel this
        // attempt, do the work, then close again.
        e.Cancel = true;
        if (_quit is not null)
            return;
        try
        {
            _quit = QuitAsync(vm, e.CloseReason);
            if (await _quit)
                Close();
        }
        catch (Exception ex)
        {
            Log.Error("ui: window close failed", ex);
            // A bug in the quit must not wedge the window open forever; let the close proceed.
            if (!_sessionEnding)
            {
                _quitWork = Task.CompletedTask;
                Close();
            }
        }
        finally
        {
            _quit = null;
        }
    }

    // A quit the user started: asks first while scripts run, then makes sure the user's settings change
    // landed, then does the quit's work. False when the user stayed, or the session end took over.
    private async Task<bool> QuitAsync(MainWindowViewModel vm, WindowCloseReason reason)
    {
        if (MainWindowCloseGuard.ShouldConfirmQuit(reason, vm.ShouldConfirmQuit()) && !await ConfirmQuitAsync(vm))
            return false;
        if (_sessionEnding || !await SettingsSavedOrQuitAnywayAsync(vm) || _sessionEnding)
            return false;
        await (_quitWork ??= RunQuitWorkAsync(vm));
        return !_sessionEnding;
    }

    // A settings change of the user's that did not land stops a quit the user started: they retry it,
    // quit anyway, or cancel and keep the app open (unsaved-edits-conventions, Quitting). The running
    // scripts are stopped only after this, so a cancelled quit leaves them running. Each round starts the
    // exit deadline afresh.
    private async Task<bool> SettingsSavedOrQuitAnywayAsync(MainWindowViewModel vm)
    {
        var retry = false;
        while (true)
        {
            vm.BeginQuit();
            if (await vm.SettingsSavedForQuitAsync(retry))
                return true;
            if (_sessionEnding)
                return false;

            var dialog = new UnsavedSettingsQuitDialog();
            try
            {
                await AskAsync(dialog);
            }
            catch (Exception ex)
            {
                // A question that cannot be shown cancels the quit; waiting is never consent to discard.
                Log.Error("quit: the unsaved-settings question could not be shown; staying open", ex);
                return false;
            }
            if (_sessionEnding)
                return false;

            switch (dialog.Choice)
            {
                case UnsavedQuitChoice.Retry:
                    retry = true;
                    continue;
                case UnsavedQuitChoice.QuitAnyway:
                    Log.Warn("quit: quitting with the settings change unsaved");
                    return true;
                default:
                    return false;
            }
        }
    }

    // A quit can come from the Dock or the app menu while this window is minimized: its questions attach
    // to a window that is showing, or stand alone, and never bring this one back (window-conventions).
    private async Task AskAsync(DialogBase question)
    {
        _quitQuestion = question;
        try { await question.ShowOverAsync(DialogBase.ShowingOwner(this, Records?.Window)); }
        finally { _quitQuestion = null; }
    }

    // The system ending the session: the same waits as any quit, within the exit deadline, and never a
    // question; a settings change that did not land is logged. The backup history is best effort, so the
    // exit does not wait for it.
    private async Task EndSessionAsync(MainWindowViewModel vm)
    {
        if (_quitWork is null)
        {
            vm.BeginQuit();
            if (!await vm.SettingsSavedForQuitAsync())
                Log.Error("quit: the session ended with the settings change unsaved");
        }
        if (vm.Exit is { } exit)
            exit.SessionEnding = true;
        await (_quitWork ??= RunQuitWorkAsync(vm));
    }

    // The quit's work: the Records window closes, the view state is saved, and the running scripts are
    // stopped, within the exit deadline, so it never holds the quit past it.
    private async Task RunQuitWorkAsync(MainWindowViewModel vm)
    {
        vm.BeginShutdown();
        // The Records window closes first, so it cannot keep the app running and its placement is in
        // the view-state save below.
        Records?.Close();

        RememberNormalGeometry();
        if (WindowState is WindowState.Normal or WindowState.Maximized
            && _normalGeometry is { } normal)
        {
            vm.CaptureWindowPlacement(
                normal.X, normal.Y, normal.Width, normal.Height,
                OperatingSystem.IsWindows() && WindowState == WindowState.Maximized);
        }

        // Persist the stored INTENT, not the live ActualWidth/ActualHeight — those may have been
        // clamped down by a small window, and saving a clamped size would lose the user's intent.
        // Falls back to the live size only if no intent was ever established (defensive; OnLoaded
        // always seeds it).
        await vm.PersistPaneSizesAsync(
            _recentWidthIntent ?? BodyGrid.ColumnDefinitions[2].ActualWidth,
            _consoleHeightIntent ?? LeftPanesGrid.RowDefinitions[2].ActualHeight);
        await vm.ShutdownAsync();
    }

    // Keeps the dispatcher running, so the work's own continuations can run, until the work is done. The
    // work keeps to the exit deadline.
    private static void RunUntilDone(Task work)
    {
        if (work.IsCompleted)
            return;

        try
        {
            var frame = new DispatcherFrame();
            work.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            Dispatcher.UIThread.PushFrame(frame);
        }
        catch (Exception ex)
        {
            Log.Error("ui: could not wait for the quit's work as the session ended", ex);
        }
    }

    private async Task<bool> ConfirmQuitAsync(MainWindowViewModel vm)
    {
        var dialog = new ConfirmDialog(
            Message.Of("quit.title"),
            Message.Of("quit.message", ("count", vm.RunningCount)),
            "quit.confirm");
        try
        {
            await AskAsync(dialog);
            return dialog.ResultTag == "confirm";
        }
        catch (Exception ex)
        {
            // A question that cannot be shown cancels the quit rather than stopping scripts unasked
            // (unsaved-edits-conventions, Quitting).
            Log.Error("quit: the stop-running-scripts question could not be shown; staying open", ex);
            return false;
        }
    }

    private void RememberNormalGeometry()
    {
        if (WindowState != WindowState.Normal)
            return;

        // Avalonia reports macOS title-bar zoom as Normal. Judge the settled native frame too,
        // otherwise the zoomed rectangle replaces the actual normal rectangle.
        var screen = Screens.ScreenFromWindow(this);
        if (screen is not null
            && WindowMetrics.IsMaximizedGeometry(
                FrameSize ?? new Size(Width, Height), screen.WorkingArea, screen.Scaling))
        {
            return;
        }

        _normalGeometry = (Position.X, Position.Y, Width, Height);
    }

    private void RememberNormalGeometryAfterNativeEvents() =>
        Dispatcher.UIThread.Post(RememberNormalGeometry);

    // Console: keep the view glued to the latest output unless the user has scrolled up to read.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedRecentEntry))
        {
            _consolePinnedToBottom = true; // a freshly-selected run starts pinned to its latest line
            _scrollConsolePending = true;
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.Theme) && sender is MainWindowViewModel vm)
        {
            // The theme is app-wide: when Settings commits a new one, every window and title bar follows.
            AppTheme.Apply(vm.Theme);
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

        if (_consoleScroll is not { } scroll)
            return;
        var distanceFromBottom = scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y;
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
        if (ViewModel is { ConsoleLines.Count: > 0 } vm)
            ConsoleList.ScrollIntoView(vm.ConsoleLines.Count - 1);
    }

    // New lines keep the console on its latest line while it is pinned there.
    private void OnConsoleLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_consolePinnedToBottom)
            _scrollConsolePending = true;
    }

    internal bool CanCopyConsoleSelection => ConsoleList.Selection.SelectedIndexes.Count > 0;

    // Keyboard and native-menu Copy copy the selected lines, whole and in order, as the console holds them.
    internal async Task CopyConsoleSelectionAsync()
    {
        try
        {
            if (ViewModel is not { } vm || Clipboard is not { } clipboard) return;
            var lines = ConsoleList.Selection.SelectedIndexes.Where(index => index >= 0 && index < vm.ConsoleLines.Count)
                .Order().Select(index => vm.ConsoleLines[index]).ToArray();
            if (lines.Length == 0) return;
            await clipboard.SetTextAsync(string.Join(Environment.NewLine, lines.Select(line => line.Text)));
        }
        catch (Exception ex)
        {
            Log.Warn("ui: copying console lines failed", ex);
        }
    }

    private async void OnConsoleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || e.KeyModifiers != ShortcutCatalog.CommandModifier(this) || !CanCopyConsoleSelection) return;
        e.Handled = true;
        await CopyConsoleSelectionAsync();
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
        }
    }

    private void OnSettingsClick(object? sender, RoutedEventArgs e) => _ = OpenSettingsAsync();

    /// <summary>The macOS app menu's Settings item; the same action as the in-window menu's.</summary>
    internal void ShowSettingsFromMenu() => OnSettingsClick(null, new RoutedEventArgs());

    /// <summary>The macOS app menu's About item; the same action as the in-window menu's.</summary>
    internal void ShowAboutFromMenu() => OnAboutClick(null, new RoutedEventArgs());

    private void OnShortcutsClick(object? sender, RoutedEventArgs e) => _ = ShowShortcutsAsync();

    private async Task OpenSettingsAsync()
    {
        try
        {
            var vm = ViewModel;
            if (vm is null)
                return;

            var draft = vm.CreateSettingsDraft();
            await SettingsDialog.EditAsync(this, draft, vm.TryApplySettingsAsync);
            vm.ResolveShellActionError("open-settings");
        }
        catch (Exception ex)
        {
            Log.Error("ui: open settings failed", ex);
            ViewModel?.ReportShellActionError("open-settings", Message.Of("shell.settingsFailed"));
        }
    }

    private async Task ShowShortcutsAsync()
    {
        try
        {
            await new ShortcutsDialog(_shortcuts).ShowBoundedAsync(this);
            ViewModel?.ResolveShellActionError("open-shortcuts");
        }
        catch (Exception ex)
        {
            Log.Error("ui: open shortcuts failed", ex);
            ViewModel?.ReportShellActionError("open-shortcuts", Message.Of("shell.shortcutsFailed"));
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
            ViewModel?.ReportShellActionError("open-about", Message.Of("shell.aboutFailed"));
        }
    }

    /// <summary>The Records window this window opens; set by the app before the window is shown.</summary>
    internal RecordsWindowHost? Records { get; set; }

    private void OnRecordsClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            Records?.ShowOrActivate(this);
            ViewModel?.ResolveShellActionError("open-records");
        }
        catch (Exception ex)
        {
            Log.Error("ui: open records failed", ex);
            ViewModel?.ReportShellActionError("open-records", Message.Of("shell.recordsFailed"));
        }
    }

    private void OnScriptDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: ScriptItem item } &&
            ViewModel?.RunScriptCommand is { } command && command.CanExecute(item))
            command.Execute(item);
    }

    private void OnScriptKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.None &&
            e.Key is Key.Enter or Key.Space && sender is ListBox { SelectedItem: ScriptItem item })
        {
            e.Handled = true;
            if (ViewModel?.RunScriptCommand is { } command && command.CanExecute(item))
                command.Execute(item);
        }
    }

    private void OnRecentDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: RecentEntry item } &&
            ViewModel?.RunOrRestartCommand is { } command && command.CanExecute(item))
            command.Execute(item);
    }

    private void OnRecentKeyDown(object? sender, KeyEventArgs e)
    {
        // Only the documented unmodified Delete and Backspace gestures act on the selected entry.
        if (e.KeyModifiers == KeyModifiers.None &&
            e.Key is Key.Delete or Key.Back && sender is ListBox { SelectedItem: RecentEntry item })
        {
            e.Handled = true;
            if (item.IsRunning)
            {
                if (ViewModel?.StopEntryCommand is { } stop && stop.CanExecute(item))
                    stop.Execute(item);
            }
            else if (ViewModel?.DismissEntryCommand is { } dismiss && dismiss.CanExecute(item))
            {
                dismiss.Execute(item);
            }
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
