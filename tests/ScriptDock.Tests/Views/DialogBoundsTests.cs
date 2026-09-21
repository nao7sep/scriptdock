using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ScriptDock.Controls;
using ScriptDock.Models;
using ScriptDock.ViewModels;
using ScriptDock.Views;
using Xunit;

namespace ScriptDock.Tests.Views;

/// <summary>
/// The shell's side of the modal-dialog conventions, measured on the real dialogs over a real owner:
/// the bands, the bound, the resize, and the body's clearance from its scroll bar. Settings measures
/// over 900px tall against a 720px main window, so every one of these has something to bite on.
/// </summary>
public sealed class DialogBoundsTests : WindowTest
{
    // Shorter than every dialog's natural height, so the bound is doing the work in each test. The
    // position matters: an owner at the top of the screen hides a centring mistake, because the
    // placement a too-tall dialog would get is clamped to the screen edge and lands on the right
    // answer by accident.
    private static Window ShortOwner() => new()
    {
        Width = 900,
        Height = 520,
        Position = new PixelPoint(120, 120),
    };

    // A ceiling, not a target: the dialog opens at its content height unless the screen is shorter
    // than that. Its owner's size is not in it — an owner that happens to be small says nothing about
    // how much room the dialog has.
    [AvaloniaFact]
    public void A_dialog_opens_no_taller_than_its_share_of_the_screen()
    {
        var owner = Show(ShortOwner());
        var dialog = Settings();

        Open(dialog, owner);

        var screen = owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary!;
        var ceiling = WindowMetrics.DialogMaxHeight(screen.WorkingArea.Height, screen.Scaling);
        Assert.True(dialog.Bounds.Height > 0, "the dialog never laid out");
        Assert.True(dialog.Bounds.Height <= ceiling + 0.5,
            $"the dialog is {dialog.Bounds.Height:F0} tall against a ceiling of {ceiling:F0}");
        Assert.Equal(ceiling, dialog.MaxHeight, 0);
    }

    // No dialog in this app is one the user settles into, so every one of them is fixed and the bound
    // stays a cap rather than an opening size (modal-dialog conventions).
    [AvaloniaFact]
    public void Every_dialog_is_fixed_because_none_of_them_is_worked_in()
    {
        var owner = Show(ShortOwner());

        foreach (var dialog in new DialogBase[]
        {
            Settings(),
            new AboutDialog(_ => true),
            new ShortcutsDialog(ShortcutCatalog.Build(owner)),
        })
        {
            Track(dialog);
            _ = dialog.ShowBoundedAsync(owner);
            Dispatcher.UIThread.RunJobs();
            dialog.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.False(dialog.CanResize, $"{dialog.GetType().Name} is resizable");
            // Shortcuts sizes to its width as well, so the flag is what matters, not the exact value.
            Assert.True(dialog.SizeToContent.HasFlag(SizeToContent.Height),
                $"{dialog.GetType().Name} stopped sizing to its content");
            Assert.True(double.IsFinite(dialog.MaxHeight), $"{dialog.GetType().Name} lost its bound");
        }
    }




    // The bound has to be on the window before it is placed. A toolkit places an owner-centred window
    // while showing it and never places it again, so a bound applied once the window is open centres
    // the unbounded height and then shrinks under it. Opened is the first moment we can look, and by
    // then the bound must already be there.
    [AvaloniaFact]
    public void The_bound_is_on_the_dialog_before_it_is_placed()
    {
        var owner = Show(ShortOwner());
        var dialog = Track(Settings());
        var boundAtOpen = double.NaN;
        dialog.Opened += (_, _) => boundAtOpen = dialog.MaxHeight;

        _ = dialog.ShowBoundedAsync(owner);
        Dispatcher.UIThread.RunJobs();

        Assert.True(double.IsFinite(boundAtOpen),
            "the dialog was already open before it was bounded, so it was placed at the wrong height");
        Assert.Equal(dialog.MaxHeight, boundAtOpen, 0);
    }

    // The point of bounding the height is that the footer survives it.
    [AvaloniaFact]
    public void The_body_takes_the_overflow_and_the_footer_stays_on_screen()
    {
        var owner = Show(ShortOwner());
        var dialog = OpenScrolling(owner);

        var body = Body(dialog);
        Assert.True(
            body.Extent.Height > body.Viewport.Height,
            "the body must be the region that overflows");

        var footer = dialog.FindControl<StackPanel>("ButtonPanel")!;
        var footerBottom = footer.TranslatePoint(new Point(0, footer.Bounds.Height), dialog)!.Value.Y;
        Assert.True(footer.Bounds.Height > 0, "the footer never laid out");
        Assert.True(
            footerBottom <= dialog.Bounds.Height + 0.5,
            $"the footer ends at {footerBottom:F0} in a {dialog.Bounds.Height:F0} dialog");
    }

    // A margin on the region rather than on its content leaves the bar short of the band's corners:
    // a gap above it where it should start, and another below.
    [AvaloniaFact]
    public void The_scroll_region_fills_its_band_on_every_side()
    {
        var owner = Show(ShortOwner());
        var dialog = OpenScrolling(owner);

        var body = Body(dialog);
        var topLeft = body.TranslatePoint(new Point(0, 0), dialog)!.Value;
        var bottomRight = body.TranslatePoint(new Point(body.Bounds.Width, body.Bounds.Height), dialog)!.Value;
        var separatorTop = dialog.FindControl<Border>("FooterSeparator")!
            .TranslatePoint(new Point(0, 0), dialog)!.Value.Y;

        // The top has no line because the OS title bar is the boundary there; the body starts under it.
        Assert.Equal(0, topLeft.X, 0);
        Assert.Equal(0, topLeft.Y, 0);
        Assert.Equal(dialog.Bounds.Width, bottomRight.X, 0);
        Assert.Equal(separatorTop, bottomRight.Y, 0);
    }

    // Without the line a long form is simply cut off above the buttons with nothing to say where the
    // content ended.
    [AvaloniaFact]
    public void The_footer_band_opens_with_a_line_and_centres_its_buttons()
    {
        var owner = Show(ShortOwner());
        var dialog = OpenScrolling(owner);

        var separator = dialog.FindControl<Border>("FooterSeparator")!;
        var footer = dialog.FindControl<StackPanel>("ButtonPanel")!;
        var separatorRect = Rect(separator, dialog);
        var footerRect = Rect(footer, dialog);

        Assert.True(separator.IsVisible && separatorRect.Height > 0, "the footer opens with no line");
        Assert.Equal(0, separatorRect.X, 0);
        Assert.Equal(dialog.Bounds.Width, separatorRect.Right, 0);

        var above = footerRect.Y - separatorRect.Bottom;
        var below = dialog.Bounds.Height - footerRect.Bottom;
        Assert.True(above > 0, "the buttons sit against the line");
        Assert.Equal(above, below, 0);
    }

    private static Rect Rect(Visual visual, Visual relativeTo)
    {
        var topLeft = visual.TranslatePoint(new Point(0, 0), relativeTo)!.Value;
        return new Rect(topLeft.X, topLeft.Y, visual.Bounds.Width, visual.Bounds.Height);
    }

    // The same inset on all four sides, rather than whatever each edge happened to inherit. The bottom
    // one lives in the scrollable extent, since the content cannot show it while scrolled off the end.
    [AvaloniaFact]
    public void The_content_is_inset_by_the_same_padding_on_every_side()
    {
        var owner = Show(ShortOwner());
        var dialog = OpenScrolling(owner);

        var body = Body(dialog);
        var presenter = dialog.GetVisualDescendants().OfType<ContentPresenter>()
            .Single(candidate => candidate.Name == "DialogContent");
        var topLeft = presenter.TranslatePoint(new Point(0, 0), body)!.Value;
        var right = body.Bounds.Width
            - presenter.TranslatePoint(new Point(presenter.Bounds.Width, 0), body)!.Value.X;
        var bottom = body.Extent.Height - presenter.Bounds.Height - topLeft.Y;

        Assert.True(topLeft.Y > 0, "the content sits flush against the top of the band");
        Assert.Equal(topLeft.Y, topLeft.X, 0);
        Assert.Equal(topLeft.Y, right, 0);
        Assert.Equal(topLeft.Y, bottom, 0);
    }

    // The original fault: the bar drew in the same band as the right-hand end of each list's Remove
    // button. It may take part of the content's inset, but it may not reach the content.
    [AvaloniaFact]
    public void The_scroll_bar_takes_the_content_inset_and_never_the_content()
    {
        var owner = Show(ShortOwner());
        var dialog = OpenScrolling(owner);

        var body = Body(dialog);
        Assert.True(
            body.Extent.Height > body.Viewport.Height,
            "the body must overflow, or there is no bar to measure against");

        var bar = body.GetVisualDescendants().OfType<ScrollBar>()
            .Single(candidate => candidate.Orientation == Orientation.Vertical && candidate.Bounds.Width > 0);
        var barLeft = bar.TranslatePoint(new Point(0, 0), dialog)!.Value.X;
        var barRight = bar.TranslatePoint(new Point(bar.Bounds.Width, 0), dialog)!.Value.X;

        // The bar belongs at the dialog's edge, where the inset is — not somewhere inside the content.
        Assert.Equal(dialog.Bounds.Width, barRight, 0);

        var presenter = dialog.GetVisualDescendants().OfType<ContentPresenter>()
            .Single(candidate => candidate.Name == "DialogContent");
        var contentRight = presenter.TranslatePoint(new Point(presenter.Bounds.Width, 0), dialog)!.Value.X;
        Assert.True(
            contentRight <= barLeft,
            $"the content reaches {contentRight:F0} and the bar starts at {barLeft:F0}");

        var covered = new List<string>();
        foreach (var control in body.GetVisualDescendants().OfType<Control>())
        {
            if (control is not (Button or ComboBox or CheckBox or TextBox or ListBox))
                continue;
            if (!control.IsEffectivelyVisible || control.Bounds.Width <= 0)
                continue;
            if (control.FindAncestorOfType<ScrollBar>() is not null)
                continue;

            var right = control.TranslatePoint(new Point(control.Bounds.Width, 0), dialog)!.Value.X;
            if (right > barLeft)
                covered.Add($"{control.GetType().Name} reaches {right:F0} past the bar at {barLeft:F0}");
        }

        Assert.True(covered.Count == 0, string.Join("; ", covered));
    }

    // The inset covers the bar by arithmetic, not by construction, so both numbers are read back here:
    // a toolkit bump that widens the bar, or a shell change that narrows the padding, fails here rather
    // than shipping a covered control. The bar clips to its own box, so that box is all it can paint.
    [AvaloniaFact]
    public void The_content_inset_is_wider_than_the_bar_it_has_to_make_room_for()
    {
        var owner = Show(ShortOwner());
        var dialog = OpenScrolling(owner);

        var declared = (double)dialog.FindResource("ScrollBarSize")!;
        var bar = Body(dialog).GetVisualDescendants().OfType<ScrollBar>()
            .Single(candidate => candidate.Orientation == Orientation.Vertical && candidate.Bounds.Width > 0);
        var presenter = dialog.GetVisualDescendants().OfType<ContentPresenter>()
            .Single(candidate => candidate.Name == "DialogContent");
        var inset = dialog.Bounds.Width
            - presenter.TranslatePoint(new Point(presenter.Bounds.Width, 0), dialog)!.Value.X;

        Assert.Equal(declared, bar.Bounds.Width, 0);
        Assert.True(bar.ClipToBounds, "the bar could paint outside the box measured here");
        Assert.True(
            inset > declared,
            $"the content is inset {inset:F0}px and the bar is {declared:F0}px wide, so it reaches the content");
    }

    // A bar that took layout space would widen the content by its own width the moment the dialog is
    // dragged tall enough to stop scrolling — a jump mid-drag.
    [AvaloniaFact]
    public void The_content_is_the_same_width_whether_the_body_scrolls_or_not()
    {
        var owner = Show(ShortOwner());
        var dialog = OpenScrolling(owner);

        var body = Body(dialog);
        var presenter = dialog.GetVisualDescendants().OfType<ContentPresenter>()
            .Single(candidate => candidate.Name == "DialogContent");

        Assert.True(body.Extent.Height > body.Viewport.Height, "the body must start out scrolling");
        var scrolling = presenter.Bounds.Width;

        // The dialog is fixed, so the bound is the only thing holding it short: lift that and
        // SizeToContent grows it to its content, which is what stops the body scrolling.
        dialog.MaxHeight = double.PositiveInfinity;
        dialog.InvalidateMeasure();
        dialog.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.False(body.Extent.Height > body.Viewport.Height, "the body should have stopped scrolling");
        Assert.Equal(scrolling, presenter.Bounds.Width, 0);
    }

    // The startup-failure notice is the application's only window: it has no owner to be bounded by,
    // and no way to be resized back if it opens taller than the display.
    [AvaloniaFact]
    public void The_startup_failure_notice_is_bounded_by_the_screen()
    {
        var notice = NoticeDialog.CreateStartupFailure(
            ScriptDock.I18n.Message.Of("startup.failedTitle"),
            ScriptDock.I18n.Message.Of("startup.failedTitle"));

        try
        {
            var screen = notice.Screens.Primary!;
            Assert.Equal(
                WindowMetrics.DialogMaxHeight(screen.WorkingArea.Height, screen.Scaling),
                notice.MaxHeight);
            Assert.True(double.IsFinite(notice.MaxHeight), "the notice is bounded by nothing");
        }
        finally
        {
            notice.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    // The same notice, as the application itself. Every other dialog here is shown over an owner, so the
    // shell keeps them out of the taskbar and centres them on that owner — both wrong for the one window
    // the user has: unlisted, it cannot be brought back once something covers it, and there is no owner
    // to centre on. The shell's own defaults are asserted alongside, because they are what makes the
    // factory's two lines mean anything.
    [AvaloniaFact]
    public void The_startup_failure_notice_takes_the_chrome_of_a_lone_window()
    {
        var notice = NoticeDialog.CreateStartupFailure(
            ScriptDock.I18n.Message.Of("startup.failedTitle"),
            ScriptDock.I18n.Message.Of("startup.failedTitle"));
        var owned = new DialogBase();

        try
        {
            Assert.False(owned.ShowInTaskbar, "an owned dialog should stay out of the taskbar");
            Assert.Equal(WindowStartupLocation.CenterOwner, owned.WindowStartupLocation);

            Assert.True(notice.ShowInTaskbar, "the app's only window has to be listed");
            Assert.Equal(WindowStartupLocation.CenterScreen, notice.WindowStartupLocation);
        }
        finally
        {
            owned.Close();
            notice.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    // Avalonia's own ShowDialog stays callable, and a new dialog that reached for it would be unbounded
    // again with nothing else to say so.
    [Fact]
    public void No_dialog_opens_through_the_unbounded_ShowDialog()
    {
        // The shell's own call is the one place the dialog is already bounded when it runs.
        var bypasses = Directory
            .EnumerateFiles(ViewsDirectory(), "*.cs")
            .Where(file => Path.GetFileName(file) != "DialogBase.axaml.cs")
            .SelectMany(file => File.ReadAllLines(file)
                .Select((line, index) => (file, number: index + 1, line))
                .Where(entry => entry.line.Contains("ShowDialog(", StringComparison.Ordinal)))
            .Select(entry => $"{Path.GetFileName(entry.file)}:{entry.number}")
            .ToList();

        Assert.True(
            bypasses.Count == 0,
            $"these open a dialog without bounding it to its owner: {string.Join(", ", bypasses)}");
    }

    private static string ViewsDirectory([CallerFilePath] string callerPath = "")
    {
        // This file: <repo>/tests/ScriptDock.Tests/Views/DialogBoundsTests.cs
        var testsViewsDir = Path.GetDirectoryName(callerPath)!;
        var repoRoot = Path.GetFullPath(Path.Combine(testsViewsDir, "..", "..", ".."));
        return Path.Combine(repoRoot, "src", "ScriptDock", "Views");
    }

    private static SettingsDialog Settings() =>
        new(new SettingsDialogViewModel(new AppConfig()), _ => true);

    private static ScrollViewer Body(Window dialog) =>
        dialog.GetVisualDescendants().OfType<ScrollViewer>()
            .First(viewer => viewer.FindAncestorOfType<ComposingTextBox>() is null
                && viewer.FindAncestorOfType<ListBox>() is null);

    /// <summary>
    /// Opens the dialog the way the app does and settles the layout. The returned task completes only
    /// when the dialog closes, which <see cref="WindowTest.Dispose"/> does at the end of the test.
    /// </summary>
    private void Open(DialogBase dialog, Window owner)
    {
        Track(dialog);
        _ = dialog.ShowBoundedAsync(owner);
        Dispatcher.UIThread.RunJobs();
        dialog.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Opens the dialog and then squeezes it until its body overflows. The headless screen is taller
    /// than any dialog in this app, so the real bound never bites here; the geometry these tests
    /// measure — the bands, the inset, the bar — is what a short screen would produce.
    /// </summary>
    private SettingsDialog OpenScrolling(Window owner)
    {
        var dialog = Settings();
        Open(dialog, owner);
        dialog.MaxHeight = dialog.Bounds.Height - 300;
        dialog.InvalidateMeasure();
        dialog.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return dialog;
    }
}
