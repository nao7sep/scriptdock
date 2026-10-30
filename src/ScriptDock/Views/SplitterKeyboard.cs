using System;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;

namespace ScriptDock.Views;

/// <summary>
/// Keyboard resizing for a pane splitter (fleet decision): the arrow keys along its axis move it 16 px, and
/// Home and End move it to either end. The pane's new size is the user's intent, as a drag's is, so a later
/// window resize keeps it. Stepping is continuous editing, so a size saved on its own is saved a short
/// delay after the last key, each key pushing the save back, or at once when focus leaves.
/// The toolkit's own 10 px step neither recorded the intent nor saved it.
/// </summary>
public static class SplitterKeyboard
{
    public const double Step = 16;

    /// <summary>How long after the last key a pending size is saved.</summary>
    public static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The pane's size after <paramref name="key"/>, or null for a key the splitter does not take.
    /// <paramref name="paneBefore"/>: the pane lies left of or above the splitter, so moving the splitter
    /// right or down grows it.
    /// </summary>
    public static double? Resize(Key key, Orientation axis, double size, double min, double max, bool paneBefore)
    {
        if (key == Key.Home)
            return paneBefore ? min : max;
        if (key == Key.End)
            return paneBefore ? max : min;

        int? direction = (axis, key) switch
        {
            (Orientation.Horizontal, Key.Right) or (Orientation.Vertical, Key.Down) => 1,
            (Orientation.Horizontal, Key.Left) or (Orientation.Vertical, Key.Up) => -1,
            _ => null,
        };
        if (direction is not { } toward)
            return null;
        return Math.Clamp(size + Step * toward * (paneBefore ? 1 : -1), min, Math.Max(min, max));
    }

    /// <summary>
    /// Handles the keys on <paramref name="splitter"/>: <paramref name="measure"/> gives the pane's current
    /// size and bounds, <paramref name="apply"/> records the new size as the intent and shows it, and
    /// <paramref name="commit"/>, when given, saves it <see cref="SaveDelay"/> after the last key or when
    /// focus leaves. The returned action saves a pending size at once, for a window that closes first.
    /// </summary>
    public static Action Attach(
        GridSplitter splitter, Orientation axis, bool paneBefore,
        Func<(double Size, double Min, double Max)> measure, Action<double> apply, Action? commit = null,
        TimeProvider? time = null)
    {
        var pending = false;
        ITimer? timer = null;

        void Commit()
        {
            timer?.Dispose();
            timer = null;
            if (!pending)
                return;
            pending = false;
            commit?.Invoke();
        }

        splitter.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            var (size, min, max) = measure();
            if (Resize(e.Key, axis, size, min, max, paneBefore) is not { } next)
                return;
            apply(next);
            e.Handled = true;
            if (commit is null)
                return;
            pending = true;
            timer?.Dispose();
            // The timer fires off the interface thread; Commit runs where the keys do.
            timer = (time ?? TimeProvider.System).CreateTimer(
                _ => Dispatcher.UIThread.Post(Commit), null, SaveDelay, Timeout.InfiniteTimeSpan);
        }, RoutingStrategies.Tunnel);

        splitter.LostFocus += (_, _) => Commit();
        return Commit;
    }
}
