using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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
/// the app. <see cref="IsDirty"/> is the draft differing from the config it was seeded from, so the
/// dialog can gate Save and prompt on discard.
/// </summary>
public sealed partial class SettingsDialogViewModel : ObservableObject
{
    private readonly List<string> _originalRoots;
    private readonly List<string> _originalExtensions;
    private readonly List<string> _originalPatterns;
    private readonly bool _originalKillProcessesOnClose;
    private readonly bool _originalRecaptureProcessesOnLaunch;
    private readonly string _originalUiFontFamily;
    private readonly ThemePreference _originalTheme;
    private readonly string _originalLanguage;

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

    // Process-lifecycle settings. NotifyPropertyChangedFor keeps IsDirty live as they toggle.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private bool _killProcessesOnClose;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private bool _recaptureProcessesOnLaunch;

    public SettingsDialogViewModel(AppConfig config)
    {
        _originalRoots = config.RootDirs.ToList();
        _originalExtensions = config.Extensions.ToList();
        _originalPatterns = config.IgnorePatterns.ToList();
        _originalKillProcessesOnClose = config.KillProcessesOnClose;
        _originalRecaptureProcessesOnLaunch = config.RecaptureProcessesOnLaunch;
        _originalUiFontFamily = config.UiFontFamily;
        _originalTheme = config.Theme;
        _theme = config.Theme;
        _originalLanguage = Languages.NormalizePreference(config.Language);
        LanguageOptions = LanguageOption.All();
        _language = LanguageOption.For(_originalLanguage, LanguageOptions);
        _killProcessesOnClose = config.KillProcessesOnClose;          // field, not property: no dirty flip during construction
        _recaptureProcessesOnLaunch = config.RecaptureProcessesOnLaunch;
        _uiFontFamily = config.UiFontFamily;

        RootDirs = new ObservableCollection<string>(_originalRoots);
        Extensions = new ObservableCollection<string>(_originalExtensions);
        IgnorePatterns = new ObservableCollection<string>(_originalPatterns);

        RootDirs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsDirty));
        Extensions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsDirty));
        IgnorePatterns.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsDirty));
    }

    public bool IsDirty =>
        !RootDirs.SequenceEqual(_originalRoots) ||
        !Extensions.SequenceEqual(_originalExtensions) ||
        !IgnorePatterns.SequenceEqual(_originalPatterns) ||
        KillProcessesOnClose != _originalKillProcessesOnClose ||
        RecaptureProcessesOnLaunch != _originalRecaptureProcessesOnLaunch ||
        UiFontFamily != _originalUiFontFamily ||
        Theme != _originalTheme ||
        Language.Value != _originalLanguage;

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

    public bool AddRootDir(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            return false;

        // Resolve to an absolute path at commit — expand a leading ~ and anchor a
        // relative entry to the home directory, never the working directory, so it
        // cannot later resolve against cwd in the scanner (storage-path-conventions).
        var resolved = ResolveRoot(trimmed);
        if (RootDirs.Any(root => PathIdentity.Same(root, resolved)))
            return false;

        RootDirs.Add(resolved);
        return true;
    }

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
        if (trimmed.Length == 0)
        {
            ExtensionErrorMessage = Message.Of("settings.extensionEmpty");
            return false;
        }

        // An extension is a single token, so reject a pasted multi-token / multi-line value rather
        // than silently forming a junk extension. This is validation, which the text-cleanup
        // convention leaves to the app; char.IsWhiteSpace also covers a tab, a stray newline, and
        // the full-width space (U+3000).
        if (trimmed.Any(char.IsWhiteSpace))
        {
            ExtensionErrorMessage = Message.Of("settings.extensionSpaces");
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
        if (trimmed.Length == 0)
        {
            PatternErrorMessage = Message.Of("settings.patternEmpty");
            return false;
        }

        // A pattern is a single-line regex. An interior line break means a multi-line paste leaked
        // in; reject it rather than flatten it — collapsing a newline to a space would silently
        // change what the regex matches. Interior spaces are left alone (a regex can match a literal
        // space in a path), so this checks only line breaks, not all whitespace.
        if (trimmed.Contains('\n') || trimmed.Contains('\r'))
        {
            PatternErrorMessage = Message.Of("settings.patternMultiline");
            return false;
        }

        if (!IsValidRegex(trimmed))
        {
            PatternErrorMessage = Message.Of("settings.patternInvalid", ("pattern", trimmed));
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

    public void RemoveRootDir(string value) => RootDirs.Remove(value);
    public void RemoveExtension(string value) => Extensions.Remove(value);
    public void RemoveIgnorePattern(string value) => IgnorePatterns.Remove(value);

    private static bool IsValidRegex(string pattern)
    {
        try
        {
            _ = new Regex(pattern);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
