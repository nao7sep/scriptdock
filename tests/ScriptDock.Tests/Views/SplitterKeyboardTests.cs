using System;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using ScriptDock.Tests.Fakes;
using Avalonia.Layout;
using ScriptDock.Views;
using Xunit;

namespace ScriptDock.Tests.Views;

public sealed class SplitterKeyboardTests
{
    [Theory]
    [InlineData(Key.Right, 380.0, true, 396.0)]   // a pane left of the splitter grows as it moves right
    [InlineData(Key.Left, 380.0, true, 364.0)]
    [InlineData(Key.Right, 380.0, false, 364.0)]  // a pane right of it shrinks
    [InlineData(Key.Home, 380.0, true, 320.0)]
    [InlineData(Key.End, 380.0, true, 640.0)]
    [InlineData(Key.Home, 380.0, false, 640.0)]
    [InlineData(Key.Left, 330.0, true, 320.0)]    // clamped at the minimum
    [InlineData(Key.Right, 630.0, true, 640.0)]   // and the maximum
    public void ColumnSplitter_MovesBySixteenOrToAnEnd(Key key, double size, bool paneBefore, double expected)
    {
        Assert.Equal(expected, SplitterKeyboard.Resize(key, Orientation.Horizontal, size, 320, 640, paneBefore));
    }

    [Theory]
    [InlineData(Key.Down, 200.0, 184.0)] // the console below its splitter shrinks as the splitter moves down
    [InlineData(Key.Up, 200.0, 216.0)]
    public void RowSplitter_TakesUpAndDown(Key key, double size, double expected)
    {
        Assert.Equal(expected, SplitterKeyboard.Resize(key, Orientation.Vertical, size, 100, 400, paneBefore: false));
    }

    [Theory]
    [InlineData(Key.Up, Orientation.Horizontal)]
    [InlineData(Key.Left, Orientation.Vertical)]
    [InlineData(Key.A, Orientation.Horizontal)]
    public void OtherKeys_AreLeftToTheWindow(Key key, Orientation axis)
    {
        Assert.Null(SplitterKeyboard.Resize(key, axis, 300, 100, 400, paneBefore: true));
    }

    [AvaloniaFact]
    public void SteppedSize_IsSavedOnceAfterTheLastKey()
    {
        var clock = new FakeTimeProvider();
        var splitter = new GridSplitter();
        var size = 300.0;
        var saves = 0;
        SplitterKeyboard.Attach(splitter, Orientation.Horizontal, paneBefore: true,
            () => (size, 100, 600), next => size = next, () => saves++, clock);

        for (var i = 0; i < 3; i++)
        {
            Press(splitter, Key.Right);
            clock.Advance(SplitterKeyboard.SaveDelay - TimeSpan.FromMilliseconds(1));
            Dispatcher.UIThread.RunJobs();
        }
        Assert.Equal(0, saves); // each key pushed the save back

        clock.Advance(TimeSpan.FromMilliseconds(1));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, saves);
        Assert.Equal(348.0, size);
    }

    [AvaloniaFact]
    public void PendingSize_IsSavedAtOnceWhenAsked_AndNotAgain()
    {
        var clock = new FakeTimeProvider();
        var splitter = new GridSplitter();
        var saves = 0;
        var saveNow = SplitterKeyboard.Attach(splitter, Orientation.Horizontal, paneBefore: true,
            () => (300, 100, 600), _ => { }, () => saves++, clock);

        saveNow();
        Assert.Equal(0, saves); // nothing stepped, nothing to save

        Press(splitter, Key.Left);
        saveNow();
        Assert.Equal(1, saves);
        Assert.Equal(0, clock.Pending);
    }

    private static void Press(GridSplitter splitter, Key key) =>
        splitter.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
}
