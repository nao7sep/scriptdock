using System;
using Avalonia.Controls;
using ScriptDock.ViewModels;

namespace ScriptDock.Views;

/// <summary>
/// Keeps the Records window to one: opening it again brings the open one forward. The main window closes it
/// before its own close saves the view state, so the Records window never keeps the app from quitting and
/// its placement is in that save.
/// </summary>
internal sealed class RecordsWindowHost(Func<RecordsWindowViewModel> createViewModel)
{
    private RecordsWindow? _window;

    /// <summary>The open Records window, if there is one.</summary>
    public RecordsWindow? Window => _window;

    public RecordsWindow ShowOrActivate()
    {
        if (_window is { } open)
        {
            WindowActivation.BringBack(open);
            return open;
        }

        var window = new RecordsWindow { DataContext = createViewModel() };
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_window, window))
                _window = null;
        };
        window.RestorePlacement();
        window.Show();
        _window = window;
        return window;
    }

    public void Close() => _window?.Close();
}

/// <summary>Bringing a window back to the reader: out of the Dock or taskbar, shown, and in front.</summary>
internal static class WindowActivation
{
    public static void BringBack(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        if (!window.IsVisible)
            window.Show();
        window.Activate();
    }
}
