using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using ScriptDock.I18n;

namespace ScriptDock.Views;

/// <summary>
/// A single-button informational dialog: a wrapped message with a Close button.
/// Used for notices the user must see once (a quarantined store) rather than
/// choices — the shared ConfirmDialog handles those.
/// </summary>
public sealed class NoticeDialog : DialogBase
{
    private NoticeDialog(Message title, Message message)
    {
        Width = 440;
        // Rendered once, as the dialog is built: it is modal, so the language cannot change under it.
        Title = Localizer.Of(title);

        SetContent(new TextBlock
        {
            Text = Localizer.Of(message),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
        });

        var buttons = SetButtons([new DialogButton("common.close", "close", DialogButtonKind.Primary) { IsDefault = true }]);
        SetInitialFocus(buttons["close"]);
    }

    public static Task ShowAsync(Window owner, Message title, Message message) =>
        new NoticeDialog(title, message).ShowBoundedAsync(owner);

    /// <summary>
    /// A startup failure notice used as the main window. Closing it ends the app. It has no owner to
    /// be bounded by, so the screen bounds it, and the lifetime shows it once it is returned.
    /// </summary>
    public static Window CreateStartupFailure(Message title, Message message)
    {
        var dialog = new NoticeDialog(title, message);
        dialog.BoundHeightToScreen();
        ShowAsOnlyWindow(dialog);
        return dialog;
    }
}
