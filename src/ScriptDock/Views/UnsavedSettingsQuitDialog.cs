using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using ScriptDock.I18n;

namespace ScriptDock.Views;

/// <summary>What the user chose when a quit could not save their settings change.</summary>
public enum UnsavedQuitChoice
{
    /// <summary>Cancel, Escape or the window's close: the quit stops and the app stays open.</summary>
    Stay,

    /// <summary>Save the change again, then quit if it lands.</summary>
    Retry,

    /// <summary>Quit without the change.</summary>
    QuitAnyway,
}

/// <summary>
/// Asked when a quit the user started could not save the user's settings change
/// (unsaved-edits-conventions, Quitting). Retry keeps the change, so it takes focus; quitting without it
/// is the destructive choice.
/// </summary>
public sealed class UnsavedSettingsQuitDialog : DialogBase
{
    // Three footer buttons, which a narrower dialog cannot fit in every language (LabelFitTests).
    internal const double DialogWidth = 480;

    internal UnsavedSettingsQuitDialog()
    {
        Width = DialogWidth;
        Title = Localizer.T("quit.unsavedTitle");

        SetContent(new TextBlock
        {
            Text = Localizer.T("quit.unsavedMessage"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
        });

        var buttons = SetButtons(
        [
            new DialogButton("common.cancel", "cancel"),
            new DialogButton("quit.retry", "retry", DialogButtonKind.Primary) { IsDefault = true },
            new DialogButton("quit.quitAnyway", "quit", DialogButtonKind.Danger),
        ]);

        SetInitialFocus(buttons["retry"]);
    }

    internal UnsavedQuitChoice Choice => ResultTag switch
    {
        "retry" => UnsavedQuitChoice.Retry,
        "quit" => UnsavedQuitChoice.QuitAnyway,
        _ => UnsavedQuitChoice.Stay,
    };

    /// <summary>Asks over <paramref name="owner"/>; every way of dismissing it settles on staying open.</summary>
    public static async Task<UnsavedQuitChoice> AskAsync(Window owner)
    {
        var dialog = new UnsavedSettingsQuitDialog();
        await dialog.ShowBoundedAsync(owner);
        return dialog.Choice;
    }
}
