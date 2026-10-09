using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using ScriptDock.I18n;
using ScriptDock.Models;
using ScriptDock.ViewModels;
using ScriptDock.Views;
using Xunit;

namespace ScriptDock.Tests.I18n;

/// <summary>
/// A label fits its control in every language (localization conventions).
///
/// The main window derives its own minimum size from the labels it is drawing, so a longer language
/// widens the window instead of clipping. The dialogs do not: they are a fixed width by design, which
/// is where a translation that grew by half actually gets cut off. So these open each fixed-width
/// dialog in all ten languages and measure the text against the room it was given, with the real font
/// and the real layout rather than by eye.
/// </summary>
public class LabelFitTests : WindowTest
{
    // A label may exceed its box by this much before it is called clipped: Skia's measurement and
    // Avalonia's arrangement round differently, and a fraction of a pixel is not a defect.
    private const double Tolerance = 1.0;

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("it")]
    [InlineData("pt-BR")]
    [InlineData("ru")]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("zh-Hans")]
    public void the_settings_dialog_clips_nothing(string tag)
    {
        using var speaking = Localizer.Speaking(tag);

        var dialog = Show(new SettingsDialog(new SettingsDialogViewModel(new AppConfig()), _ => Task.FromResult(true)));

        AssertNothingClipped(dialog, tag, atLeast: 8);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("it")]
    [InlineData("pt-BR")]
    [InlineData("ru")]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("zh-Hans")]
    public void the_shortcuts_dialog_clips_nothing(string tag)
    {
        using var speaking = Localizer.Speaking(tag);

        var owner = Show(new Window());
        var dialog = Show(new ShortcutsDialog(ShortcutCatalog.Build(owner)));

        AssertNothingClipped(dialog, tag, atLeast: 8);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("it")]
    [InlineData("pt-BR")]
    [InlineData("ru")]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("zh-Hans")]
    public void the_about_dialog_clips_nothing(string tag)
    {
        using var speaking = Localizer.Speaking(tag);

        var dialog = Show(new AboutDialog(_ => true));

        // The About dialog's body wraps by design; its name, version, licence and button do not.
        AssertNothingClipped(dialog, tag, atLeast: 4);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("it")]
    [InlineData("pt-BR")]
    [InlineData("ru")]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("zh-Hans")]
    public void the_unsaved_settings_quit_dialog_clips_nothing(string tag)
    {
        using var speaking = Localizer.Speaking(tag);

        var dialog = Show(new UnsavedSettingsQuitDialog());

        // Its message wraps; its three button labels do not.
        AssertNothingClipped(dialog, tag, atLeast: 3);

        // A footer of three buttons wider than the dialog would run past its edge, which the labels alone
        // cannot show: every button lies inside the footer's 24px side margins.
        const double footerMargin = 24;
        var buttons = dialog.GetVisualDescendants().OfType<Button>().ToList();
        Assert.Equal(3, buttons.Count);
        foreach (var button in buttons)
        {
            var left = button.TranslatePoint(new Point(0, 0), dialog)!.Value.X;
            var right = left + button.Bounds.Width;
            Assert.True(
                left >= footerMargin - Tolerance && right <= UnsavedSettingsQuitDialog.DialogWidth - footerMargin + Tolerance,
                $"{tag}: “{button.Content}” spans {left:F0}–{right:F0}px in a {UnsavedSettingsQuitDialog.DialogWidth}px dialog");
        }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("it")]
    [InlineData("pt-BR")]
    [InlineData("ru")]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("zh-Hans")]
    public void the_discard_prompt_clips_nothing(string tag)
    {
        using var speaking = Localizer.Speaking(tag);

        var dialog = Show(new ConfirmDialog(
            Message.Of("dialog.discardTitle"), Message.Of("dialog.discardMessage"), "dialog.discard", "dialog.keepEditing"));

        AssertNothingClipped(dialog, tag, atLeast: 2);
        const double footerMargin = 24;
        foreach (var button in dialog.GetVisualDescendants().OfType<Button>())
        {
            var left = button.TranslatePoint(new Point(0, 0), dialog)!.Value.X;
            Assert.True(
                left >= footerMargin - Tolerance && left + button.Bounds.Width <= ConfirmDialog.DialogWidth - footerMargin + Tolerance,
                $"{tag}: “{button.Content}” spans {left:F0}–{left + button.Bounds.Width:F0}px in a {ConfirmDialog.DialogWidth}px dialog");
        }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("it")]
    [InlineData("pt-BR")]
    [InlineData("ru")]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("zh-Hans")]
    public void the_first_run_folder_dialog_clips_nothing(string tag)
    {
        using var speaking = Localizer.Speaking(tag);

        var dialog = Show(new FirstRunFolderDialog());

        // Its message wraps; its two button labels do not.
        AssertNothingClipped(dialog, tag, atLeast: 2);
    }

    private static void AssertNothingClipped(Visual root, string tag, int atLeast)
    {
        var clipped = new List<string>();
        var measured = 0;

        foreach (var text in root.GetVisualDescendants().OfType<TextBlock>())
        {
            // Text that wraps or is deliberately trimmed is not clipped; neither is a label that was
            // never given a size, which happens to anything not currently on screen.
            if (text.Text is not { Length: > 0 } words)
                continue;
            if (text.TextWrapping != TextWrapping.NoWrap || text.TextTrimming != TextTrimming.None)
                continue;
            if (text.Bounds.Width <= 0)
                continue;

            measured++;
            var needed = Measure(words, text);
            if (needed > text.Bounds.Width + Tolerance)
                clipped.Add($"“{words}” needs {needed:F0}px in {text.Bounds.Width:F0}px");
        }

        // A gate that measures nothing would pass forever: if a redesign makes every label wrap or
        // trim, this says so rather than quietly stopping work.
        Assert.True(
            measured >= atLeast,
            $"{tag}: only {measured} labels were measured, fewer than the {atLeast} this dialog has; the check is not looking at anything.");
        Assert.True(clipped.Count == 0, $"{tag}: {string.Join("; ", clipped)}");
    }

    private static double Measure(string words, TextBlock text) =>
        new FormattedText(
            words,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight),
            text.FontSize,
            null).Width;
}
