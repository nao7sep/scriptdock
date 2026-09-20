using System;
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
    private readonly Func<SettingsDialogViewModel, bool> _trySave;

    public SettingsDialog(SettingsDialogViewModel draft, Func<SettingsDialogViewModel, bool> trySave)
    {
        _draft = draft;
        _trySave = trySave;
        I18n.Localized.SetTitle(this, "settings.title");
        Width = 520;

        var content = new SettingsView { DataContext = draft };
        SetContent(content);
        SetInitialFocus(content.InitialFocusControl);

        var buttons = SetButtons(
        [
            new DialogButton("common.cancel", "cancel"),
            new DialogButton("common.save", "save", DialogButtonKind.Primary) { IsDefault = true },
        ]);

        // Commit gating: Save is enabled only when the draft differs from the saved config.
        buttons["save"].Bind(
            Button.IsEnabledProperty,
            new Binding(nameof(SettingsDialogViewModel.IsDirty)) { Source = draft });
    }

    public bool Saved => ResultTag == "save";

    protected override bool HasUnsavedChanges => _draft.IsDirty;

    protected override bool TryCommit(string tag)
    {
        if (tag != "save")
            return true;

        _draft.SaveErrorMessage = null;
        if (_trySave(_draft))
            return true;

        _draft.SaveErrorMessage = I18n.Message.Of("settings.saveFailed");
        return false;
    }

    public static async Task<bool> EditAsync(
        Window owner,
        SettingsDialogViewModel draft,
        Func<SettingsDialogViewModel, bool> trySave)
    {
        var dialog = new SettingsDialog(draft, trySave);
        await dialog.ShowDialog(owner);
        return dialog.Saved;
    }
}
