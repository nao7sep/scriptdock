using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ScriptDock.Models;
using ScriptDock.ViewModels;

namespace ScriptDock.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
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

        await vm.InitializeAsync();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        var vm = ViewModel;
        if (vm is null)
            return;

        vm.PersistWindowBounds(Position.X, Position.Y, Width, Height);
        vm.Shutdown();
    }

    // Cmd+R rescans. Handled at the window level so it works regardless of which list has focus.
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.R && e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            e.Handled = true;
            if (ViewModel?.RescanCommand.CanExecute(null) == true)
                ViewModel.RescanCommand.Execute(null);
        }
    }

    private void OnScriptDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: ScriptItem item })
            ViewModel?.RunCommand.Execute(item);
    }

    private void OnRecentDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: RecentItem item })
            ViewModel?.RunRecentCommand.Execute(item);
    }

    private void OnScriptKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space && sender is ListBox { SelectedItem: ScriptItem item })
        {
            e.Handled = true;
            ViewModel?.RunCommand.Execute(item);
        }
    }

    private void OnProcessKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && sender is ListBox { SelectedItem: ProcessItem item })
        {
            e.Handled = true;
            if (item.State == RunState.Running)
                ViewModel?.TerminateProcessCommand.Execute(item);
            else
                ViewModel?.DismissProcessCommand.Execute(item);
        }
    }

    // Keep a saved position usable if the display layout changed: fall back to a small
    // offset when the saved point sits off every screen's working area.
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
