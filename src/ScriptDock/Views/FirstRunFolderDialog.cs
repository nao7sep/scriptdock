using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using ScriptDock.I18n;

namespace ScriptDock.Views;

/// <summary>
/// First-run setup (config-sets-conventions): ScriptDock cannot list anything until it has a folder to scan,
/// so a launch without one asks for it in a single step. Choosing opens the folder picker; Not Now, Escape and
/// the window's close write nothing.
/// </summary>
public sealed class FirstRunFolderDialog : DialogBase
{
    internal const double DialogWidth = 440;

    internal FirstRunFolderDialog()
    {
        Width = DialogWidth;
        Title = Localizer.T("firstRun.title");

        SetContent(new TextBlock
        {
            Text = Localizer.T("firstRun.message"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
        });

        var buttons = SetButtons(
        [
            new DialogButton("firstRun.notNow", "cancel"),
            new DialogButton("firstRun.choose", "choose", DialogButtonKind.Primary) { IsDefault = true },
        ]);

        SetInitialFocus(buttons["choose"]);
    }

    /// <summary>Asks over <paramref name="owner"/>; true when the user chose to pick a folder.</summary>
    public static async Task<bool> AskAsync(Window owner)
    {
        var dialog = new FirstRunFolderDialog();
        await dialog.ShowBoundedAsync(owner);
        return dialog.ResultTag == "choose";
    }
}
