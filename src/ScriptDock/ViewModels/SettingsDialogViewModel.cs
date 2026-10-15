using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
    private readonly List<Task<bool>> _pickedRoots = [];

    public ObservableCollection<string> RootDirs { get; }
    public ObservableCollection<EditableItem> Extensions { get; }
    public ObservableCollection<EditableItem> IgnorePatterns { get; }

    // The stored sets the app could not apply, kept unchanged until the user saves a value for them, and
    // the sets this draft has chosen a value for, a reset to the built-in included.
    private readonly HashSet<string> _kept;
    private readonly HashSet<string> _chosen = new(StringComparer.Ordinal);

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

    // The zone times are shown in, chosen from the list and applied on Save with its neighbours.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private TimeZoneOption _timeZone;

    // Whether the computer's appearance is dark now, which the System theme entry names. The view keeps
    // it current while the dialog is open.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeSystemLabel))]
    private bool _systemThemeIsDark;

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

    public SettingsDialogViewModel(AppConfig config, IReadOnlyCollection<string>? keptSets = null)
    {
        _kept = new HashSet<string>(keptSets ?? [], StringComparer.Ordinal);
        _theme = config.Theme;
        LanguageOptions = LanguageOption.All();
        _language = LanguageOption.For(Languages.NormalizePreference(config.Language), LanguageOptions);
        TimeZoneOptions = TimeZoneOption.All();
        _timeZone = TimeZoneOption.For(config.TimeZone, TimeZoneOptions);
        _uiFontFamily = config.UiFontFamily;

        RootDirs = new ObservableCollection<string>(config.RootDirs);
        Extensions = new ObservableCollection<EditableItem>(config.Extensions.Select(text => new EditableItem(text)));
        IgnorePatterns = new ObservableCollection<EditableItem>(config.IgnorePatterns.Select(text => new EditableItem(text)));
        _opened = ToConfig();

        RootDirs.CollectionChanged += (_, _) => Chose("rootDirs");
        TrackRows(Extensions, "extensions");
        TrackRows(IgnorePatterns, "ignorePatterns");
    }

    // A row added, removed or edited is a value chosen for its set.
    private void TrackRows(ObservableCollection<EditableItem> rows, string set)
    {
        void Watch(EditableItem row) => row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditableItem.Text))
                Chose(set);
        };
        foreach (var row in rows)
            Watch(row);
        rows.CollectionChanged += (_, e) =>
        {
            foreach (var row in e.NewItems?.OfType<EditableItem>() ?? [])
                Watch(row);
            Chose(set);
        };
    }

    partial void OnLanguageChanged(LanguageOption value) => Chose("language");
    partial void OnTimeZoneChanged(TimeZoneOption value) => Chose("timeZone");
    partial void OnThemeChanged(ThemePreference value) => Chose("theme");
    partial void OnUiFontFamilyChanged(string value) => Chose("uiFontFamily");

    private void Chose(string set)
    {
        _chosen.Add(set);
        OnPropertyChanged(nameof(IsDirty));
    }

    /// <summary>The sets this draft chose a value for, which replace any stored entry the app kept.</summary>
    public IReadOnlyCollection<string> ChosenSets => _chosen;

    // A list as stored: each row trimmed, blank rows dropped, and one entry per member as the scanner
    // matches them, ignoring case.
    private static List<string> Rows(IEnumerable<EditableItem> rows, Func<string, string> stored) =>
        rows.Select(row => row.Text.Trim()).Where(text => text.Length > 0).Select(stored)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The draft as a config, its text cleaned. It holds no hidden scripts: the dialog does not edit them.</summary>
    public AppConfig ToConfig() => new()
    {
        RootDirs = RootDirs.ToList(),
        Extensions = Rows(Extensions, ConfigSets.Extension),
        IgnorePatterns = Rows(IgnorePatterns, text => text),
        UiFontFamily = TextCleanup.SingleLine(UiFontFamily),
        Theme = Theme,
        Language = Language.Value,
        TimeZone = TimeZone.Value,
    };

    /// <summary>The draft differs from the opened settings, or chooses a value for a set the app kept
    /// unchanged, which a reset to the built-in replaces.</summary>
    public bool IsDirty => !ConfigSets.Same(ToConfig(), _opened) || _chosen.Overlaps(_kept);

    /// <summary>
    /// The check Save runs: each non-blank row passes its set's check, or carries a message naming it on
    /// its own row, and the extensions keep at least one. False holds Save.
    /// </summary>
    public bool Validate()
    {
        var valid = true;
        foreach (var row in Extensions)
            valid &= Check(row, ConfigSets.ExtensionError);
        foreach (var row in IgnorePatterns)
            valid &= Check(row, ConfigSets.PatternError);
        if (valid && ToConfig().Extensions.Count == 0)
        {
            ExtensionErrorMessage = Message.Of("settings.extensionsRequired");
            valid = false;
        }
        return valid;

        static bool Check(EditableItem row, Func<string, Message?> error)
        {
            var trimmed = row.Text.Trim();
            row.ErrorMessage = trimmed.Length > 0 && error(trimmed) is { } reason
                ? Message.Of("settings.rowError", ("item", trimmed), ("reason", reason))
                : null;
            return row.ErrorMessage is null;
        }
    }

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

    /// <summary>The zone list: System first, with the zone it resolves to now, then the platform's zones.</summary>
    public IReadOnlyList<TimeZoneOption> TimeZoneOptions { get; }

    /// <summary>The System theme entry, with the appearance it resolves to now (app-chrome-conventions, Theme).</summary>
    public string ThemeSystemLabel => Localizer.T("settings.themeSystem",
        ("theme", Localizer.T(SystemThemeIsDark ? "settings.themeDark" : "settings.themeLight")));

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
    public Task<bool> AddPickedRootDirAsync(string value) => AddPickedRootDirAsync(value, PathIdentity.Key);

    public Task<bool> AddPickedRootDirsAsync(IEnumerable<string> values) => AddPickedRootDirsAsync(values, PathIdentity.Key);

    internal Task<bool> AddPickedRootDirAsync(string value, Func<string, string> key) => AddPickedRootDirsAsync([value], key);

    private Task<bool> AddPickedRootDirsAsync(IEnumerable<string> values, Func<string, string> key)
    {
        var work = AddPickedRootsCoreAsync(values.ToList(), key);
        _pickedRoots.Add(work);
        return SettleAsync();
        async Task<bool> SettleAsync()
        {
            try { return await work; }
            finally { _pickedRoots.Remove(work); }
        }
    }

    internal async Task WaitForPickedRootsAsync()
    {
        while (_pickedRoots.Count > 0)
        {
            var pending = _pickedRoots.ToArray();
            try { await Task.WhenAll(pending); }
            finally { foreach (var work in pending) _pickedRoots.Remove(work); }
        }
    }

    private async Task<bool> AddPickedRootsCoreAsync(IReadOnlyList<string> values, Func<string, string> key)
    {
        var added = false;
        foreach (var value in values) added |= await AddPickedRootCoreAsync(value, key);
        return added;
    }

    private async Task<bool> AddPickedRootCoreAsync(string value, Func<string, string> key)
    {
        if (value.Length == 0)
            return false;

        var resolved = ResolveRoot(value);
        while (true)
        {
            var roots = RootDirs.ToList();
            var duplicate = await Task.Run(() =>
            {
                var pickedKey = key(resolved);
                return roots.Any(root => PathIdentity.Comparer.Equals(key(root), pickedKey));
            });
            // A second picker result may arrive while identities resolve. Adopt only a current draft.
            if (!roots.SequenceEqual(RootDirs, StringComparer.Ordinal)) continue;
            if (duplicate) return false;
            RootDirs.Add(resolved);
            return true;
        }
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

        trimmed = ConfigSets.Extension(trimmed);

        // Dedup case-insensitively to match how the scanner compares extensions
        // (OrdinalIgnoreCase, mirroring the case-insensitive filesystems it runs on), so
        // ".command" and ".Command" are one entry rather than two that match the same files.
        if (Extensions.Any(e => string.Equals(ConfigSets.Extension(e.Text.Trim()), trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            ExtensionErrorMessage = Message.Of("settings.extensionDuplicate");
            return false;
        }

        Extensions.Add(new EditableItem(trimmed));
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
        if (IgnorePatterns.Any(p => string.Equals(p.Text.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            PatternErrorMessage = Message.Of("settings.patternDuplicate");
            return false;
        }

        IgnorePatterns.Add(new EditableItem(trimmed));
        PatternErrorMessage = null;
        return true;
    }

    // A reset chooses the built-in, which replaces a stored set the app could not apply.
    public void ResetExtensions()
    {
        Extensions.Clear();
        Extensions.Add(new EditableItem(ConfigDefaults.DefaultExtension));
        ExtensionErrorMessage = null;
        Chose("extensions");
    }

    public void ResetIgnorePatterns()
    {
        IgnorePatterns.Clear();
        foreach (var pattern in ConfigDefaults.BuiltInIgnorePatterns)
            IgnorePatterns.Add(new EditableItem(pattern));
        PatternErrorMessage = null;
        Chose("ignorePatterns");
    }

    public void RemoveRootDir(string value) => RootDirs.Remove(value);
    public void RemoveExtension(EditableItem row) => Extensions.Remove(row);
    public void RemoveIgnorePattern(EditableItem row) => IgnorePatterns.Remove(row);
}
