using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.ViewModels;

namespace ScriptDock.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;
    private IReadOnlyList<ShortcutItem> _shortcuts = [];

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            var vm = ViewModel;
            if (vm is null)
                return;

            if (vm.SavedBounds is { } bounds && bounds.Width > 0 && bounds.Height > 0)
            {
                Width = bounds.Width;
                Height = bounds.Height;
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = ClampToVisible(bounds);
            }

            if (vm.SavedRecentWidth is { } recentWidth && recentWidth > 80)
                ListsGrid.ColumnDefinitions[2].Width = new GridLength(recentWidth, GridUnitType.Pixel);
            if (vm.SavedConsoleHeight is { } consoleHeight && consoleHeight > 60)
                BodyGrid.RowDefinitions[2].Height = new GridLength(consoleHeight, GridUnitType.Pixel);

            // One catalog drives both the live accelerators and the help modal, with the command key
            // (Cmd on macOS, Ctrl on Windows) resolved by the framework — so a label never describes a
            // binding that does not exist, and the menu hints match the live bindings.
            _shortcuts = ShortcutCatalog.Build(this);
            SettingsMenuItem.InputGesture = GestureFor(ShortcutAction.OpenSettings);
            ShortcutsMenuItem.InputGesture = GestureFor(ShortcutAction.ShowShortcuts);

            await vm.InitializeAsync();
        }
        catch (Exception ex)
        {
            Log.Error("ui: window load failed", ex);
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        try
        {
            var vm = ViewModel;
            if (vm is null)
                return;

            vm.PersistWindowBounds(Position.X, Position.Y, Width, Height);
            vm.PersistPaneSizes(ListsGrid.ColumnDefinitions[2].ActualWidth, BodyGrid.RowDefinitions[2].ActualHeight);
            vm.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Error("ui: window close failed", ex);
        }
    }

    // Application accelerators matched against the catalog, so a key can only fire a binding the help
    // modal also shows. Control-owned keys (Enter/Space/Delete/arrows) are handled by the lists.
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

    private KeyGesture? GestureFor(ShortcutAction action) =>
        _shortcuts.FirstOrDefault(s => s.Action == action)?.Gesture;

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
        if (e.Key == Key.Delete && sender is ListBox { SelectedItem: DockEntry item })
        {
            e.Handled = true;
            if (item.IsRunning)
                ViewModel?.StopEntryCommand.Execute(item);
            else
                ViewModel?.DismissEntryCommand.Execute(item);
        }
    }

    // Keep a saved position usable if the display layout changed: fall back to a small offset when
    // the saved point sits off every screen's working area.
    private PixelPoint ClampToVisible(WindowBounds bounds)
    {
        var point = new PixelPoint((int)bounds.X, (int)bounds.Y);

        var screens = Screens;
        if (screens is null || screens.All.Count == 0)
            return point;

        foreach (var screen in screens.All)
        {
            var area = screen.WorkingArea;
            if (point.X >= area.X - 50 && point.X <= area.X + area.Width - 50 &&
                point.Y >= area.Y && point.Y <= area.Y + area.Height - 20)
            {
                return point;
            }
        }

        return new PixelPoint(60, 60);
    }
}
