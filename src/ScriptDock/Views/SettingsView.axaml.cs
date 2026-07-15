using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ScriptDock.Services;
using ScriptDock.ViewModels;

namespace ScriptDock.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private SettingsDialogViewModel? Vm => DataContext as SettingsDialogViewModel;

    // Enter adds the typed value. The IME guard lives in ComposingTextBox, which raises Submitted only
    // on a real Enter — never the IME's candidate-commit (Key.ImeProcessed) — per the
    // text-input-ime-conventions, so a composed Enter never adds a half-finished item.
    private void OnExtSubmitted(object? sender, RoutedEventArgs e) => AddExtension();

    private void OnPatternSubmitted(object? sender, RoutedEventArgs e) => AddPattern();

    // Root directories are chosen with the OS folder picker. The picker is an external boundary,
    // so its work is wrapped per the crash guard's Layer-1 contract — a failure logs, never crashes.
    private async void OnAddRootClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Vm is null)
                return;

            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null)
                return;

            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Add root directory",
                AllowMultiple = true,
            });

            // The view model resolves, de-duplicates, and validates each path; non-local
            // picks (a virtual location with no filesystem path) are skipped.
            foreach (var folder in folders)
            {
                if (folder.TryGetLocalPath() is { } path)
                    Vm.AddRootDir(path);
            }
        }
        catch (Exception ex)
        {
            Log.Error("ui: add root directory failed", ex);
        }
    }

    private void OnAddExtClick(object? sender, RoutedEventArgs e) => AddExtension();
    private void OnAddPatternClick(object? sender, RoutedEventArgs e) => AddPattern();

    private void OnRemoveRootClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null && RootList.SelectedItem is string value)
            Vm.RemoveRootDir(value);
    }

    private void OnRemoveExtClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null && ExtList.SelectedItem is string value)
            Vm.RemoveExtension(value);
    }

    private void OnRemovePatternClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null && PatternList.SelectedItem is string value)
            Vm.RemoveIgnorePattern(value);
    }

    private void AddExtension()
    {
        if (Vm is null)
            return;
        if (Vm.AddExtension(ExtEntry.Text ?? string.Empty))
            ExtEntry.Text = string.Empty;
        ExtEntry.Focus();
    }

    private void AddPattern()
    {
        if (Vm is null)
            return;
        if (Vm.AddIgnorePattern(PatternEntry.Text ?? string.Empty))
            PatternEntry.Text = string.Empty;
        PatternEntry.Focus();
    }
}
