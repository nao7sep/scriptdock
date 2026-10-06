using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using ScriptDock.I18n;
using ScriptDock.Models;
using ScriptDock.Storage;
using ScriptDock.Services;

namespace ScriptDock.ViewModels;

/// <summary>
/// Editable draft of the configuration shown by the settings dialog: root directories,
/// file extensions, ignore patterns, and the process-lifecycle settings. Add validates (non-empty,
/// no duplicate; an extension rejects whitespace and is normalised to a leading dot; a pattern must
/// be a single line and must compile) — all at commit time, never mid-keystroke, per the
/// text-input-ime-conventions; rejection is validation, which the text-cleanup conventions leave to
/// the app. <see cref="IsDirty"/> is the cleaned draft differing from the config it was seeded from, so
/// the dialog can gate Save and prompt on discard.
/// </summary>
public sealed partial class SettingsDialogViewModel : ObservableObject
{
    private readonly AppConfig _opened;

    public ObservableCollection<string> RootDirs { get; }
    public ObservableCollection<string> Extensions { get; }
    public ObservableCollection<string> IgnorePatterns { get; }

    // The dialog's own messages are held as keys and rendered where they are shown, so none of them
    // is assembled in one language (localization conventions).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExtensionError))]
    [NotifyPropertyChangedFor(nameof(HasExtensionError))]
    [NotifyPropertyChangedFor(nameof(ExtensionItemStatus))]
    private Message? _extensionErrorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PatternError))]
    [NotifyPropertyChangedFor(nameof(HasPatternError))]
    [NotifyPropertyChangedFor(nameof(PatternItemStatus))]
    private Message? _patternErrorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveError))]
    [NotifyPropertyChangedFor(nameof(HasSaveError))]
    private Message? _saveErrorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RootPickerResult))]
    [NotifyPropertyChangedFor(nameof(HasRootPickerResult))]
    private Message? _rootPickerResultMessage;

    // The interface language, chosen from the list and applied on Save with its neighbours.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private LanguageOption _language;

    // UI (chrome) font family. Family only; blank = the bundled default.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string _uiFontFamily = string.Empty;

    // The app theme, shown as a radio group bound to the three IsTheme* flags.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyPropertyChangedFor(nameof(IsThemeSystem))]
    [NotifyPropertyChangedFor(nameof(IsThemeLight))]
    [NotifyPropertyChangedFor(nameof(IsThemeDark))]
    private ThemePreference _theme;

    public SettingsDialogViewModel(AppConfig config)
    {
        _theme = config.Theme;
        LanguageOptions = LanguageOption.All();
        _language = LanguageOption.For(Languages.NormalizePreference(config.Language), LanguageOptions);
        _uiFontFamily = config.UiFontFamily;

        RootDirs = new ObservableCollection<string>(config.RootDirs);
        Extensions = new ObservableCollection<string>(config.Extensions);
        IgnorePatterns = new ObservableCollection<string>(config.IgnorePatterns);
        _opened = ToConfig();

        RootDirs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsDirty));
        Extensions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsDirty));
        IgnorePatterns.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsDirty));
    }

    /// <summary>The draft as a config, its text cleaned. It holds no hidden scripts: the dialog does not edit them.</summary>
    public AppConfig ToConfig() => new()
    {
        RootDirs = RootDirs.ToList(),
        Extensions = Extensions.ToList(),
        IgnorePatterns = IgnorePatterns.ToList(),
        UiFontFamily = TextCleanup.SingleLine(UiFontFamily),
        Theme = Theme,
        Language = Language.Value,
    };

    public bool IsDirty => !ConfigSets.Same(ToConfig(), _opened);

    // One flag per radio: checking one selects its theme; the others clear through the group.
    public bool IsThemeSystem
    {
        get => Theme == ThemePreference.System;
        set { if (value) Theme = ThemePreference.System; }
    }

    public bool IsThemeLight
    {
        get => Theme == ThemePreference.Light;
        set { if (value) Theme = ThemePreference.Light; }
    }

    public bool IsThemeDark
    {
        get => Theme == ThemePreference.Dark;
        set { if (value) Theme = ThemePreference.Dark; }
    }

    /// <summary>The language list: System first, then each language under its own name.</summary>
    public IReadOnlyList<LanguageOption> LanguageOptions { get; }

    public string ExtensionError => Localizer.Current.Of(ExtensionErrorMessage) ?? string.Empty;
    public string PatternError => Localizer.Current.Of(PatternErrorMessage) ?? string.Empty;
    public string SaveError => Localizer.Current.Of(SaveErrorMessage) ?? string.Empty;
    public string RootPickerResult => Localizer.Current.Of(RootPickerResultMessage) ?? string.Empty;

    public bool HasExtensionError => ExtensionErrorMessage is not null;
    public bool HasPatternError => PatternErrorMessage is not null;
    public bool HasSaveError => SaveErrorMessage is not null;
    public bool HasRootPickerResult => RootPickerResultMessage is not null;

    // A code a screen reader reads as the field's status, not a sentence: it stays as it is.
    public string ExtensionItemStatus => HasExtensionError ? "Invalid" : string.Empty;
    public string PatternItemStatus => HasPatternError ? "Invalid" : string.Empty;

    public void ReportRootPickerFailure(Exception error) =>
        RootPickerResultMessage = FailurePresentation.RootPicker(error);

    public void ResolveRootPickerFailure() => RootPickerResultMessage = null;

    /// <summary>Adds a path returned by the native picker without trimming legal filename bytes.</summary>
    public bool AddPickedRootDir(string value)
    {
        if (value.Length == 0)
            return false;

        var resolved = ResolveRoot(value);
        if (RootDirs.Any(root => PathIdentity.Same(root, resolved)))
            return false;

        RootDirs.Add(resolved);
        return true;
    }

    // Expand a leading ~ / ~/ and make the value absolute against the home directory (never the
    // working directory), shared with the storage-root resolver via HomePath so both anchor paths
    // identically. The folder need not exist yet — the scanner reports a missing root as
    // inaccessible rather than failing.
    private static string ResolveRoot(string value) =>
        HomePath.AnchorToHome(value, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public bool AddExtension(string value)
    {
        var trimmed = value.Trim();
        if (ConfigSets.ExtensionError(trimmed) is { } error)
        {
            ExtensionErrorMessage = error;
            return false;
        }

        if (!trimmed.StartsWith('.'))
            trimmed = "." + trimmed;

        // Dedup case-insensitively to match how the scanner compares extensions
        // (OrdinalIgnoreCase, mirroring the case-insensitive filesystems it runs on), so
        // ".command" and ".Command" are one entry rather than two that match the same files.
        if (Extensions.Any(e => string.Equals(e, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            ExtensionErrorMessage = Message.Of("settings.extensionDuplicate");
            return false;
        }

        Extensions.Add(trimmed);
        ExtensionErrorMessage = null;
        return true;
    }

    public bool AddIgnorePattern(string value)
    {
        var trimmed = value.Trim();
        if (ConfigSets.PatternError(trimmed) is { } error)
        {
            PatternErrorMessage = error;
            return false;
        }

        // Dedup case-insensitively to match how the scanner matches patterns (IgnoreRules compiles
        // them IgnoreCase), so "/Node_modules/" and "/node_modules/" are one entry, not two that
        // prune the same directories.
        if (IgnorePatterns.Any(p => string.Equals(p, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            PatternErrorMessage = Message.Of("settings.patternDuplicate");
            return false;
        }

        IgnorePatterns.Add(trimmed);
        PatternErrorMessage = null;
        return true;
    }

    public void ResetExtensions()
    {
        Extensions.Clear();
        Extensions.Add(ConfigDefaults.DefaultExtension);
        ExtensionErrorMessage = null;
    }

    public void ResetIgnorePatterns()
    {
        IgnorePatterns.Clear();
        foreach (var pattern in ConfigDefaults.BuiltInIgnorePatterns)
            IgnorePatterns.Add(pattern);
        PatternErrorMessage = null;
    }

    public void RemoveRootDir(string value) => RootDirs.Remove(value);
    public void RemoveExtension(string value) => Extensions.Remove(value);
    public void RemoveIgnorePattern(string value) => IgnorePatterns.Remove(value);
}
