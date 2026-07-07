using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Data;
using ScriptDock.ViewModels;

namespace ScriptDock.Views;

/// <summary>
/// Edits the scan configuration (root directories, extensions, ignore patterns) on the
/// shared <see cref="DialogBase"/> shell. Save commits and is enabled only while the draft
/// differs from the saved config; Cancel/Escape/close run the discard guard when the draft
/// is dirty. The dialog owns no data — the caller reads the edited lists off the
/// <see cref="SettingsDialogViewModel"/> it passed in.
/// </summary>
public sealed class SettingsDialog : DialogBase
{
    private readonly SettingsDialogViewModel _draft;

    public SettingsDialog(SettingsDialogViewModel draft)
    {
        _draft = draft;
        Title = "Settings";
        Width = 520;

        SetContent(new SettingsView { DataContext = draft });

        var buttons = SetButtons(
        [
            new DialogButton("Cancel", "cancel"),
            new DialogButton("Save", "save", DialogButtonKind.Primary),
        ]);

        // Commit gating: Save is enabled only when the draft differs from the saved config.
        buttons["save"].Bind(
            Button.IsEnabledProperty,
            new Binding(nameof(SettingsDialogViewModel.IsDirty)) { Source = draft });
    }

    public bool Saved => ResultTag == "save";

    protected override bool HasUnsavedChanges => _draft.IsDirty;

    public static async Task<bool> EditAsync(Window owner, SettingsDialogViewModel draft)
    {
        var dialog = new SettingsDialog(draft);
        await dialog.ShowDialog(owner);
        return dialog.Saved;
    }
}
