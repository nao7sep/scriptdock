using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using ScriptDock.Services;
using ScriptDock.ViewModels;

namespace ScriptDock.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        // The System theme entry names the computer's appearance, kept current while the dialog is open.
        AttachedToVisualTree += (_, _) =>
        {
            ShowSystemAppearance();
            if (Avalonia.Application.Current?.PlatformSettings is { } platform)
                platform.ColorValuesChanged += OnColorValuesChanged;
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (Avalonia.Application.Current?.PlatformSettings is { } platform)
                platform.ColorValuesChanged -= OnColorValuesChanged;
        };
        DataContextChanged += (_, _) => ShowSystemAppearance();
    }

    private void OnColorValuesChanged(object? sender, Avalonia.Platform.PlatformColorValues values) => ShowSystemAppearance();

    private void ShowSystemAppearance()
    {
        if (Vm is { } vm && Avalonia.Application.Current?.PlatformSettings?.GetColorValues() is { } colors)
            vm.SystemThemeIsDark = colors.ThemeVariant == Avalonia.Platform.PlatformThemeVariant.Dark;
    }

    public Control InitialFocusControl => UiFontEntry;

    private SettingsDialogViewModel? Vm => DataContext as SettingsDialogViewModel;

    // Enter adds the typed value. The IME guard lives in ComposingTextBox, which raises Submitted only
    // on a real Enter — never the IME's candidate-commit (Key.ImeProcessed) — per the
    // text-input-ime-conventions, so a composed Enter never adds a half-finished item.
    private void OnExtSubmitted(object? sender, RoutedEventArgs e) => AddExtension();

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
                Title = I18n.Localizer.T("settings.pickRootTitle"),
                AllowMultiple = true,
            });
            Vm.ResolveRootPickerFailure();

            // The view model resolves, de-duplicates, and validates each path; non-local
            // picks (a virtual location with no filesystem path) are skipped.
            await Vm.AddPickedRootDirsAsync(folders.Select(folder => folder.TryGetLocalPath()).OfType<string>());
        }
        catch (Exception ex)
        {
            Log.Error("ui: add root directory failed", ex);
            Vm?.ReportRootPickerFailure(ex);
        }
    }

    private void OnCloseRootPickerResult(object? sender, RoutedEventArgs e) =>
        Vm?.ResolveRootPickerFailure();

    private void OnResetExtensionsClick(object? sender, RoutedEventArgs e) => Vm?.ResetExtensions();
    private void OnResetIgnorePatternsClick(object? sender, RoutedEventArgs e) => Vm?.ResetIgnorePatterns();

    private void OnAddExtClick(object? sender, RoutedEventArgs e) => AddExtension();
    private void OnAddPatternClick(object? sender, RoutedEventArgs e) => AddPattern();

    private void OnRemoveRootClick(object? sender, RoutedEventArgs e) => RemoveSelectedRoot();

    // Delete and Backspace remove the selected folder, as they dismiss the selected Recent run.
    private void OnRootListKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key is Avalonia.Input.Key.Delete or Avalonia.Input.Key.Back && e.KeyModifiers == Avalonia.Input.KeyModifiers.None)
            e.Handled = RemoveSelectedRoot();
    }

    private bool RemoveSelectedRoot()
    {
        if (Vm is null || RootList.SelectedItem is not string value)
            return false;
        Vm.RemoveRootDir(value);
        return true;
    }

    private void OnRemoveRowClick(object? sender, RoutedEventArgs e)
    {
        switch ((sender as Control)?.DataContext)
        {
            case EditableItem row:
                Vm?.RemoveExtension(row);
                break;
            case IgnorePatternRow row:
                Vm?.RemoveIgnorePattern(row);
                break;
        }
    }

    private void AddExtension()
    {
        if (Vm is null)
            return;
        if (Vm.AddExtension(ExtEntry.Text ?? string.Empty))
            ExtEntry.Text = string.Empty;
        ExtEntry.Focus();
    }

    // The new row's field takes focus once the list has laid it out, for the pattern to be typed.
    private void AddPattern()
    {
        if (Vm is null)
            return;
        var row = Vm.AddIgnorePattern();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            PatternList.ContainerFromItem(row)?.FindDescendantOfType<Controls.ComposingTextBox>()?.Focus(),
            Avalonia.Threading.DispatcherPriority.Loaded);
    }
}
