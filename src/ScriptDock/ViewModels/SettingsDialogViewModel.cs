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
/// file extensions, ignore patterns, appearance, language, display zone and exit-code policy.
/// The extensions are one comma-separated field kept as typed; adding an ignore pattern appends a blank
/// row. Save validates the extensions field and the edited rows, dropping blank items and marking invalid
/// ones, never validating mid-keystroke. <see cref="IsDirty"/> is the cleaned draft differing from the config it was seeded from, so
/// the dialog can gate Save and prompt on discard.
/// </summary>
public sealed partial class SettingsDialogViewModel : ObservableObject
{
    private readonly AppConfig _opened;
    // Picked folders resolve one batch at a time, in pick order; the commit awaits the last.
    private Task<bool> _picks = Task.FromResult(false);

    public ObservableCollection<string> RootDirs { get; }
    public ObservableCollection<IgnorePatternRow> IgnorePatterns { get; }

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

    // The extensions as typed: short single tokens, so one field with its items separated by commas
    // (config-sets-conventions, A list the user types). It keeps what was typed; Save checks it.
    [ObservableProperty]
    private string _extensionsText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveError))]
    [NotifyPropertyChangedFor(nameof(HasSaveError))]
    private Message? _saveErrorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RootPickerResult))]
    [NotifyPropertyChangedFor(nameof(HasRootPickerResult))]
    private Message? _rootPickerResultMessage;

    // The tab shown, General first. Switching it is cheap, so it follows the tab list's focus.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabIndex))]
    [NotifyPropertyChangedFor(nameof(IsGeneralTab))]
    [NotifyPropertyChangedFor(nameof(IsIgnorePatternsTab))]
    private SettingsTab _tab;

    /// <summary>The tab as the tab list holds it.</summary>
    public int TabIndex
    {
        get => (int)Tab;
        set { if (Enum.IsDefined((SettingsTab)value)) Tab = (SettingsTab)value; }
    }

    public bool IsGeneralTab => Tab == SettingsTab.General;
    public bool IsIgnorePatternsTab => Tab == SettingsTab.IgnorePatterns;

    // The interface language, chosen from the list and applied on Save with its neighbours.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private LanguageOption _language;

    // The zone times are shown in, chosen from the list and applied on Save with its neighbours.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private TimeZoneOption _timeZone;

    // Whether an exit code other than 0 reads as an error, applied on Save with its neighbours.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private bool _nonZeroExitIsError;

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
        _nonZeroExitIsError = config.NonZeroExitIsError;

        RootDirs = new ObservableCollection<string>(config.RootDirs);
        _extensionsText = string.Join(", ", config.Extensions);
        IgnorePatterns = new ObservableCollection<IgnorePatternRow>(config.IgnorePatterns.Select(pattern => new IgnorePatternRow(pattern)));
        _opened = ToConfig();

        RootDirs.CollectionChanged += (_, _) => Chose("rootDirs");
        TrackRows(IgnorePatterns, "ignorePatterns", nameof(IgnorePatternRow.Text), nameof(IgnorePatternRow.Kind));
    }

    // A row added, removed or edited is a value chosen for its set.
    private void TrackRows<T>(ObservableCollection<T> rows, string set, params string[] edits) where T : ObservableObject
    {
        void Watch(T row) => row.PropertyChanged += (_, e) =>
        {
            if (edits.Contains(e.PropertyName))
                Chose(set);
        };
        foreach (var row in rows)
            Watch(row);
        rows.CollectionChanged += (_, e) =>
        {
            foreach (var row in e.NewItems?.OfType<T>() ?? [])
                Watch(row);
            Chose(set);
        };
    }

    partial void OnLanguageChanged(LanguageOption value) => Chose("language");
    partial void OnTimeZoneChanged(TimeZoneOption value) => Chose("timeZone");
    partial void OnThemeChanged(ThemePreference value) => Chose("theme");
    partial void OnUiFontFamilyChanged(string value) => Chose("uiFontFamily");
    partial void OnNonZeroExitIsErrorChanged(bool value) => Chose("nonZeroExitIsError");

    // Editing the field clears its message until the next Save checks it again.
    partial void OnExtensionsTextChanged(string value)
    {
        ExtensionErrorMessage = null;
        Chose("extensions");
    }
    private void Chose(string set)
    {
        _chosen.Add(set);
        OnPropertyChanged(nameof(IsDirty));
    }

    /// <summary>The sets this draft chose a value for, which replace any stored entry the app kept.</summary>
    public IReadOnlyCollection<string> ChosenSets => _chosen;

    // The field's items: each trimmed, blank ones (a doubled or trailing comma) dropped. A Chinese or
    // Japanese input method types a full-width or ideographic comma, which separates items too.
    private static readonly char[] ExtensionSeparators = [',', '，', '、'];

    private IEnumerable<string> ExtensionItems =>
        ExtensionsText.Split(ExtensionSeparators).Select(item => item.Trim()).Where(item => item.Length > 0);

    /// <summary>The draft as a config, its text cleaned. It holds no hidden scripts: the dialog does not edit them.</summary>
    public AppConfig ToConfig() => new()
    {
        RootDirs = RootDirs.ToList(),
        // Each with its leading dot, one entry per extension as the scanner matches them, ignoring case.
        Extensions = ExtensionItems.Select(ConfigSets.Extension).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        // One entry per pattern as the scanner matches them, ignoring case.
        IgnorePatterns = IgnorePatterns.Select(row => row.ToPattern()).OfType<IgnorePattern>()
            .DistinctBy(ConfigSets.PatternKey, StringComparer.OrdinalIgnoreCase).ToList(),
        UiFontFamily = TextCleanup.SingleLine(UiFontFamily),
        Theme = Theme,
        Language = Language.Value,
        TimeZone = TimeZone.Value,
        NonZeroExitIsError = NonZeroExitIsError,
    };

    /// <summary>The draft differs from the opened settings, or chooses a value for a set the app kept
    /// unchanged, which a reset to the built-in replaces.</summary>
    public bool IsDirty => !ConfigSets.Same(ToConfig(), _opened) || _chosen.Overlaps(_kept);

    /// <summary>
    /// The check Save runs: the extensions field holds at least one extension and each of its items passes,
    /// or the field's message names the first that does not; each non-blank pattern row passes its check,
    /// or carries a message naming it on its own row. False holds Save and shows the first tab with a
    /// message, so a failing item is never left out of sight.
    /// </summary>
    public bool Validate()
    {
        ExtensionErrorMessage = ExtensionsProblem();
        var general = ExtensionErrorMessage is null;

        var patterns = true;
        foreach (var row in IgnorePatterns)
            patterns &= Checked(row.Text, trimmed => ConfigSets.PatternError(trimmed, row.Kind), message => row.ErrorMessage = message);

        if (!general)
            Tab = SettingsTab.General;
        else if (!patterns)
            Tab = SettingsTab.IgnorePatterns;
        return general && patterns;

        static bool Checked(string text, Func<string, Message?> error, Action<Message?> mark)
        {
            var trimmed = text.Trim();
            var message = trimmed.Length > 0 && error(trimmed) is { } reason ? Named(trimmed, reason) : null;
            mark(message);
            return message is null;
        }
    }

    // The first item the extensions field cannot store, named with why. "sh" and ".sh" are one extension,
    // and so are ".sh" and ".SH", as the scanner matches them.
    private Message? ExtensionsProblem()
    {
        var items = ExtensionItems.ToList();
        if (items.Count == 0)
            return Message.Of("settings.extensionsRequired");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (ConfigSets.ExtensionError(item) is { } reason)
                return Named(item, reason);
            if (!seen.Add(ConfigSets.Extension(item)))
                return Named(item, Message.Of("settings.extensionDuplicate"));
        }
        return null;
    }

    private static Message Named(string item, Message reason) =>
        Message.Of("settings.rowError", ("item", item), ("reason", reason));

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
    public string SaveError => Localizer.Current.Of(SaveErrorMessage) ?? string.Empty;
    public string RootPickerResult => Localizer.Current.Of(RootPickerResultMessage) ?? string.Empty;

    public bool HasExtensionError => ExtensionErrorMessage is not null;
    public bool HasSaveError => SaveErrorMessage is not null;
    public bool HasRootPickerResult => RootPickerResultMessage is not null;

    // A code a screen reader reads as the field's status, not a sentence: it stays as it is.
    public string ExtensionItemStatus => HasExtensionError ? "Invalid" : string.Empty;

    public void ReportRootPickerFailure(Exception error) =>
        RootPickerResultMessage = FailurePresentation.RootPicker(error);

    public void ResolveRootPickerFailure() => RootPickerResultMessage = null;

    /// <summary>Adds a path returned by the native picker without trimming legal filename bytes.</summary>
    public Task<bool> AddPickedRootDirAsync(string value) => AddPickedRootDirAsync(value, PathIdentity.Key);

    public Task<bool> AddPickedRootDirsAsync(IEnumerable<string> values) => AddPickedRootDirsAsync(values, PathIdentity.Key);

    internal Task<bool> AddPickedRootDirAsync(string value, Func<string, string> key) => AddPickedRootDirsAsync([value], key);

    private Task<bool> AddPickedRootDirsAsync(IEnumerable<string> values, Func<string, string> key) =>
        _picks = AddPickedRootsAfterAsync(_picks, values.ToList(), key);

    /// <summary>Waits for the folders already picked to be added; a failed last pick fails the commit.</summary>
    internal Task WaitForPickedRootsAsync() => _picks;

    private async Task<bool> AddPickedRootsAfterAsync(Task previous, IReadOnlyList<string> values, Func<string, string> key)
    {
        try { await previous; }
        catch { /* the earlier pick's caller reports its own failure */ }
        var added = false;
        foreach (var value in values) added |= await AddPickedRootCoreAsync(value, key);
        return added;
    }

    private async Task<bool> AddPickedRootCoreAsync(string value, Func<string, string> key)
    {
        if (value.Length == 0)
            return false;

        var resolved = ResolveRoot(value);
        var roots = RootDirs.ToList();
        var duplicate = await Task.Run(() =>
        {
            var pickedKey = key(resolved);
            return roots.Any(root => PathIdentity.Comparer.Equals(key(root), pickedKey));
        });
        if (duplicate) return false;
        RootDirs.Add(resolved);
        return true;
    }

    // Expand a leading ~ / ~/ and make the value absolute against the home directory (never the
    // working directory), shared with the storage-root resolver via HomePath so both anchor paths
    // identically. The folder need not exist yet — the scanner reports a missing root as
    // inaccessible rather than failing.
    private static string ResolveRoot(string value) =>
        HomePath.AnchorToHome(value, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>Adds a blank wildcard row for the user to type into; left blank, Save drops it.</summary>
    public IgnorePatternRow AddIgnorePattern()
    {
        var row = new IgnorePatternRow(new IgnorePattern(string.Empty));
        IgnorePatterns.Add(row);
        return row;
    }

    // A reset chooses the built-in, which replaces a stored set the app could not apply.
    public void ResetExtensions()
    {
        ExtensionsText = ConfigDefaults.DefaultExtension;
        ExtensionErrorMessage = null;
        Chose("extensions");
    }

    public void ResetIgnorePatterns()
    {
        IgnorePatterns.Clear();
        foreach (var pattern in ConfigDefaults.BuiltInIgnorePatterns)
            IgnorePatterns.Add(new IgnorePatternRow(pattern));
        Chose("ignorePatterns");
    }

    public void RemoveRootDir(string value) => RootDirs.Remove(value);
    public void RemoveIgnorePattern(IgnorePatternRow row) => IgnorePatterns.Remove(row);
}

/// <summary>The Settings tabs, in their order: everything else, then the ignore patterns, which fill theirs.</summary>
public enum SettingsTab { General, IgnorePatterns }
