using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ScriptDock.ViewModels;

namespace ScriptDock.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private SettingsDialogViewModel? Vm => DataContext as SettingsDialogViewModel;

    // IME composition guard: while composing, Enter commits the IME candidate and surfaces
    // as Key.ImeProcessed, never Key.Enter — so acting only on Key.Enter is the guard the
    // text-input-ime-conventions require: a composed Enter must never add an item.
    private void OnRootKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        AddRoot();
    }

    private void OnExtKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        AddExtension();
    }

    private void OnPatternKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        AddPattern();
    }

    private void OnAddRootClick(object? sender, RoutedEventArgs e) => AddRoot();
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

    private void AddRoot()
    {
        if (Vm is null)
            return;
        if (Vm.AddRootDir(RootEntry.Text ?? string.Empty))
            RootEntry.Text = string.Empty;
        RootEntry.Focus();
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
