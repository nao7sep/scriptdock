using System;
using Avalonia.Controls;
using ScriptDock.ViewModels;

namespace ScriptDock.Views;

/// <summary>
/// Keeps the Records window to one: opening it again brings the open one forward. The main window closes it
/// before its own close saves the view state, so the Records window never keeps the app from quitting and
/// its placement is in that save. Opened at the reader's request, it comes up as the key window, and closing
/// it while it is key hands the keyboard back to the window it was opened from (window-conventions).
/// </summary>
internal sealed class RecordsWindowHost(Func<RecordsWindowViewModel> createViewModel)
{
    private RecordsWindow? _window;

    /// <summary>The open Records window, if there is one.</summary>
    public RecordsWindow? Window => _window;

    public RecordsWindow ShowOrActivate(Window? opener = null)
    {
        if (_window is { } open)
        {
            WindowActivation.BringBack(open);
            return open;
        }

        var window = new RecordsWindow { DataContext = createViewModel() };
        var wasKey = false;
        window.Closing += (_, _) => wasKey = window.IsActive;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_window, window))
                _window = null;
            // Only a visible, unminimized opener: closing never restores a window the reader put away.
            if (wasKey && opener is { IsVisible: true } && opener.WindowState != WindowState.Minimized)
                opener.Activate();
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
