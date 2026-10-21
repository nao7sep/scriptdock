using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using ScriptDock.ViewModels;

namespace ScriptDock.Views;

/// <summary>
/// Edits the settings on the shared <see cref="DialogBase"/> shell, in two tabs chosen from a strip in the
/// band above the body: General, and the ignore patterns, whose list fills its tab. Save commits and is enabled only while the draft
/// differs from the saved config; Cancel/Escape/close run the discard guard when the draft
/// is dirty. The dialog owns no data — the caller reads the edited lists off the
/// <see cref="SettingsDialogViewModel"/> it passed in.
/// </summary>
public sealed class SettingsDialog : DialogBase
{
    private readonly SettingsDialogViewModel _draft;
    private readonly Func<SettingsDialogViewModel, Task<bool>> _trySave;

    public SettingsDialog(SettingsDialogViewModel draft, Func<SettingsDialogViewModel, Task<bool>> trySave)
    {
        _draft = draft;
        _trySave = trySave;
        I18n.Localized.SetTitle(this, "settings.title");
        Width = 560;

        var tabs = new TabStrip { DataContext = draft };
        foreach (var key in (string[])["settings.tabGeneral", "settings.tabIgnorePatterns"])
        {
            var tab = new TabStripItem();
            I18n.Localized.SetContent(tab, key);
            tabs.Items.Add(tab);
        }
        tabs.Bind(SelectingItemsControl.SelectedIndexProperty,
            new Binding(nameof(SettingsDialogViewModel.TabIndex)) { Mode = BindingMode.TwoWay });
        I18n.Localized.SetAutomationName(tabs, "settings.tabs");
        Tablist.Attach(tabs);
        Tabs = tabs;
        SetBand(tabs);
        KeepOpeningHeight();

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

    /// <summary>The tab strip in the dialog's band.</summary>
    internal TabStrip Tabs { get; }

    protected override bool HasUnsavedChanges => _draft.IsDirty;

    protected override async Task<bool> TryCommit(string tag)
    {
        if (tag != "save")
            return true;

        _draft.SaveErrorMessage = null;
        // A row that fails its check is marked on its own row and holds Save.
        if (!_draft.Validate())
            return false;
        if (await _trySave(_draft))
            return true;

        _draft.SaveErrorMessage = I18n.Message.Of("settings.saveFailed");
        return false;
    }

    public static async Task<bool> EditAsync(
        Window owner,
        SettingsDialogViewModel draft,
        Func<SettingsDialogViewModel, Task<bool>> trySave)
    {
        var dialog = new SettingsDialog(draft, trySave);
        await dialog.ShowBoundedAsync(owner);
        return dialog.Saved;
    }
}
