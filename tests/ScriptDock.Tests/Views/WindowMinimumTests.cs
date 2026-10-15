using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ScriptDock.I18n;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.Tests.Fakes;
using ScriptDock.ViewModels;
using ScriptDock.Views;
using Xunit;

namespace ScriptDock.Tests.Views;

/// <summary>
/// The windows' minimums follow their chrome as layout leaves it, read rather than measured, so a language
/// change moves them and leaves every header button in a slot of its own size (window-conventions,
/// Content-based minimum size). Measuring the live headers left German slots in Japanese on the Mac.
/// </summary>
public sealed class WindowMinimumTests : WindowTest
{
    private static void Settle(Window window)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static MainWindow NewMain()
    {
        var config = new AppConfig();
        var state = new AppState();
        return new MainWindow
        {
            DataContext = new MainWindowViewModel(
                new FakeConfigStore { Value = config }, new FakeJsonStore<AppState> { Value = state }, new FakeJsonStore<KnownPaths>(),
                new FakeRecordStore(), config, state, new KnownPaths(), new ScriptScanner(), new FakeProcessRunner()),
        };
    }

    private static double Needs(MainWindow window, string header, string title, string actions) =>
        WindowMetrics.HeaderWidth(window.FindControl<Border>(header)!, window.FindControl<Control>(title)!,
            window.FindControl<StackPanel>(actions)!);

    private static void AssertButtonsSitInTheirOwnSlots(MainWindow window)
    {
        foreach (var name in new[] { "ScriptsActions", "RecentActions" })
        {
            var row = window.FindControl<StackPanel>(name)!;
            var shown = row.Children.Where(child => child.IsVisible).ToList();
            foreach (var child in shown.OfType<Button>())
                Assert.Equal(child.DesiredSize.Width, child.Bounds.Width, 0.5);
            foreach (var (left, right) in shown.Zip(shown.Skip(1)))
                Assert.Equal(left.Bounds.Right + row.Spacing, right.Bounds.Left, 0.5);
        }
    }

    [AvaloniaFact]
    public void A_language_round_trip_moves_the_pane_minimums_and_keeps_every_header_button_whole()
    {
        using var japanese = Localizer.Speaking("ja");
        var window = Show(NewMain());
        var body = window.FindControl<Grid>("BodyGrid")!;
        Settle(window);
        var recentJapanese = body.ColumnDefinitions[2].MinWidth;
        var windowJapanese = window.MinWidth;
        Assert.True(recentJapanese >= Needs(window, "RecentHeader", "RecentTitle", "RecentActions") - 0.5);
        Assert.True(body.ColumnDefinitions[0].MinWidth >= Needs(window, "ScriptsHeader", "ScriptsTitle", "ScriptsActions") - 0.5);

        using (Localizer.Speaking("de"))
        {
            Settle(window);
            Assert.True(window.MinWidth > windowJapanese, "German's longer labels did not widen the minimum.");
            Assert.True(body.ColumnDefinitions[2].MinWidth >= Needs(window, "RecentHeader", "RecentTitle", "RecentActions") - 0.5);
            AssertButtonsSitInTheirOwnSlots(window);
        }

        Settle(window);
        Assert.Equal(recentJapanese, body.ColumnDefinitions[2].MinWidth, 0.5);
        Assert.Equal(windowJapanese, window.MinWidth, 0.5);
        AssertButtonsSitInTheirOwnSlots(window);
        var title = window.FindControl<TextBlock>("RecentTitle")!;
        Assert.Equal(title.DesiredSize.Width, title.Bounds.Width, 0.5); // the title is not cut short
    }

    [AvaloniaFact]
    public void A_row_needs_its_shown_children_its_gaps_and_its_margin()
    {
        var row = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 0, 16, 0),
            Children =
            {
                new Border { Width = 40 },
                new Border { Width = 60 },
                new Border { Width = 99, IsVisible = false },
            },
        };
        var window = Show(new Window { Content = new Border { Width = 50, Child = row } });
        Settle(window);

        // The panel itself is held to 50, but its children asked for their own widths.
        Assert.Equal(40 + 8 + 60 + 16, WindowMetrics.RowWidth(row));
    }

    [AvaloniaFact]
    public void The_records_window_minimum_height_follows_its_filters_as_laid_out()
    {
        var store = new FakeJsonStore<AppState>();
        var window = Show(new RecordsWindow { DataContext = new RecordsWindowViewModel(new FakeRecordReader(), store, store.Value, new FakeTimeProvider()) });
        Settle(window);
        var band = window.FindControl<Border>("FiltersBand")!;

        Assert.Equal(RecordsLayout.MinWindowHeight(band.DesiredSize.Height), window.MinHeight, 0.5);
    }
}
