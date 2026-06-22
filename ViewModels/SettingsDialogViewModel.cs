using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using ScriptDock.Models;
using ScriptDock.Storage;

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

    public ObservableCollection<string> RootDirs { get; }
    public ObservableCollection<string> Extensions { get; }
    public ObservableCollection<string> IgnorePatterns { get; }

    [ObservableProperty]
    private string _extensionError = string.Empty;

    [ObservableProperty]
    private string _patternError = string.Empty;

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
        _killProcessesOnClose = config.KillProcessesOnClose;          // field, not property: no dirty flip during construction
        _recaptureProcessesOnLaunch = config.RecaptureProcessesOnLaunch;

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
        RecaptureProcessesOnLaunch != _originalRecaptureProcessesOnLaunch;

    public bool AddRootDir(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            return false;

        // Resolve to an absolute path at commit — expand a leading ~ and anchor a
        // relative entry to the home directory, never the working directory, so it
        // cannot later resolve against cwd in the scanner (storage-path-conventions).
        var resolved = ResolveRoot(trimmed);
        if (RootDirs.Contains(resolved))
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
            return false;

        // An extension is a single token, so reject a pasted multi-token / multi-line value rather
        // than silently forming a junk extension. This is validation, which the text-cleanup
        // convention leaves to the app; char.IsWhiteSpace also covers a tab, a stray newline, and
        // the full-width space (U+3000).
        if (trimmed.Any(char.IsWhiteSpace))
        {
            ExtensionError = "An extension can’t contain spaces or line breaks.";
            return false;
        }

        if (!trimmed.StartsWith('.'))
            trimmed = "." + trimmed;

        // Dedup case-insensitively to match how the scanner compares extensions
        // (OrdinalIgnoreCase, mirroring the case-insensitive filesystems it runs on), so
        // ".command" and ".Command" are one entry rather than two that match the same files.
        if (Extensions.Any(e => string.Equals(e, trimmed, StringComparison.OrdinalIgnoreCase)))
            return false;

        Extensions.Add(trimmed);
        ExtensionError = string.Empty;
        return true;
    }

    public bool AddIgnorePattern(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            return false;

        // A pattern is a single-line regex. An interior line break means a multi-line paste leaked
        // in; reject it rather than flatten it — collapsing a newline to a space would silently
        // change what the regex matches. Interior spaces are left alone (a regex can match a literal
        // space in a path), so this checks only line breaks, not all whitespace.
        if (trimmed.Contains('\n') || trimmed.Contains('\r'))
        {
            PatternError = "A pattern must be a single line.";
            return false;
        }

        if (!IsValidRegex(trimmed))
        {
            PatternError = $"Not a valid pattern: {trimmed}";
            return false;
        }

        // Dedup case-insensitively to match how the scanner matches patterns (IgnoreRules compiles
        // them IgnoreCase), so "/Node_modules/" and "/node_modules/" are one entry, not two that
        // prune the same directories.
        if (IgnorePatterns.Any(p => string.Equals(p, trimmed, StringComparison.OrdinalIgnoreCase)))
            return false;

        IgnorePatterns.Add(trimmed);
        PatternError = string.Empty;
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
