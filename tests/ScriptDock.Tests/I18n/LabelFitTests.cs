using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
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
public class LabelFitTests
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

        var dialog = new SettingsDialog(new SettingsDialogViewModel(new AppConfig()), _ => true);
        dialog.Show();
        Dispatcher.UIThread.RunJobs();

        AssertNothingClipped(dialog, tag, atLeast: 8);
        dialog.Close();
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

        var owner = new Window();
        owner.Show();
        var dialog = new ShortcutsDialog(ShortcutCatalog.Build(owner));
        dialog.Show();
        Dispatcher.UIThread.RunJobs();

        AssertNothingClipped(dialog, tag, atLeast: 8);
        dialog.Close();
        owner.Close();
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

        var dialog = new AboutDialog(_ => true);
        dialog.Show();
        Dispatcher.UIThread.RunJobs();

        // The About dialog's body wraps by design; its name, version, licence and button do not.
        AssertNothingClipped(dialog, tag, atLeast: 4);
        dialog.Close();
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
