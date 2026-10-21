using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using ScriptDock.I18n;
using ScriptDock.Models;

namespace ScriptDock.ViewModels;

/// <summary>
/// One row of the ignore-pattern list, editable in place (config-sets-conventions, In the interface): its
/// text and how that text reads. A built-in keeps its id while the row still holds that built-in, and shows
/// its note in the interface language; a row edited away from it is the user's own and has none, and one
/// edited to a built-in's pattern is that built-in again. Its check runs on Save; a failing row carries a
/// message that names it, shown on the row.
/// </summary>
public sealed partial class IgnorePatternRow : ObservableObject
{
    public IgnorePatternRow(IgnorePattern pattern)
    {
        _text = pattern.Pattern;
        _kind = pattern.Kind;
        Id = pattern.Id;
    }

    [ObservableProperty]
    private string _text;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KindIndex))]
    private IgnorePatternKind _kind;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private Message? _errorMessage;

    /// <summary>The built-in this row holds, by id, or null for a pattern of the user's own.</summary>
    public string? Id { get; private set; }

    /// <summary>The kind as the row's list shows it: wildcard first, then regular expression.</summary>
    public int KindIndex
    {
        get => (int)Kind;
        set { if (Enum.IsDefined((IgnorePatternKind)value)) Kind = (IgnorePatternKind)value; }
    }

    /// <summary>The built-in's note in the interface language; empty for a pattern of the user's own.</summary>
    public string Note => Id is { } id && ConfigDefaults.IgnoreNoteKeys.TryGetValue(id, out var key) ? Localizer.T(key) : string.Empty;

    public bool HasNote => Note.Length > 0;

    public string Error => Localizer.Current.Of(ErrorMessage) ?? string.Empty;
    public bool HasError => ErrorMessage is not null;

    /// <summary>The row as stored, or null when it is blank, which Save drops.</summary>
    public IgnorePattern? ToPattern()
    {
        var trimmed = Text.Trim();
        return trimmed.Length == 0 ? null : new IgnorePattern(TextCleanup.SingleLine(Text), Kind, Id);
    }

    // Editing a row clears its mark until the next Save checks it again, and decides which built-in, if
    // any, the row now holds.
    partial void OnTextChanged(string value) => Edited();
    partial void OnKindChanged(IgnorePatternKind value) => Edited();

    private void Edited()
    {
        ErrorMessage = null;
        var trimmed = Text.Trim();
        Id = ConfigDefaults.BuiltInIgnorePatterns.FirstOrDefault(builtIn =>
            builtIn.Kind == Kind && string.Equals(builtIn.Pattern, trimmed, StringComparison.OrdinalIgnoreCase))?.Id;
        OnPropertyChanged(nameof(Id));
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(HasNote));
    }
}
