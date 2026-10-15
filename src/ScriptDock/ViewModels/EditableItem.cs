using CommunityToolkit.Mvvm.ComponentModel;
using ScriptDock.I18n;

namespace ScriptDock.ViewModels;

/// <summary>
/// One row of a list the user types, editable in place (config-sets-conventions, In the interface). Its
/// check runs on Save; a failing row carries a message that names it, shown on the row.
/// </summary>
public sealed partial class EditableItem(string text) : ObservableObject
{
    [ObservableProperty]
    private string _text = text;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private Message? _errorMessage;

    public string Error => Localizer.Current.Of(ErrorMessage) ?? string.Empty;
    public bool HasError => ErrorMessage is not null;

    // Editing a row clears its mark until the next Save checks it again.
    partial void OnTextChanged(string value) => ErrorMessage = null;

    /// <summary>Renders the row's message again after a language change.</summary>
    public void Refresh() => OnPropertyChanged(nameof(Error));
}
