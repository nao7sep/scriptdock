using System;
using System.IO;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using ScriptDock.Models;

namespace ScriptDock.ViewModels;

/// <summary>
/// Editable draft of the scan configuration shown by the settings dialog: root
/// directories, file extensions, and ignore patterns. Add validates (non-empty, no
/// duplicate; a pattern must compile; an extension is normalised to a leading dot at
/// commit time — never mid-keystroke, per the text-input-ime-conventions). <see
/// cref="IsDirty"/> is the draft differing from the config it was seeded from, so the
/// dialog can gate Save and prompt on discard.
/// </summary>
public sealed partial class SettingsDialogViewModel : ObservableObject
{
    private readonly List<string> _originalRoots;
    private readonly List<string> _originalExtensions;
    private readonly List<string> _originalPatterns;

    public ObservableCollection<string> RootDirs { get; }
    public ObservableCollection<string> Extensions { get; }
    public ObservableCollection<string> IgnorePatterns { get; }

    [ObservableProperty]
    private string _patternError = string.Empty;

    public SettingsDialogViewModel(AppConfig config)
    {
        _originalRoots = config.RootDirs.ToList();
        _originalExtensions = config.Extensions.ToList();
        _originalPatterns = config.IgnorePatterns.ToList();

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
        !IgnorePatterns.SequenceEqual(_originalPatterns);

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

    // Expand a leading ~ / ~/ and make the value absolute against the home
    // directory. The folder need not exist yet — the scanner reports a missing
    // root as inaccessible rather than failing.
    private static string ResolveRoot(string value)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string expanded;
        if (value == "~")
            expanded = home;
        else if (value.StartsWith("~/", StringComparison.Ordinal) ||
                 value.StartsWith("~" + Path.DirectorySeparatorChar))
            expanded = Path.Combine(home, value[2..]);
        else
            expanded = value;

        return Path.IsPathRooted(expanded)
            ? Path.GetFullPath(expanded)
            : Path.GetFullPath(Path.Combine(home, expanded));
    }

    public bool AddExtension(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            return false;

        if (!trimmed.StartsWith('.'))
            trimmed = "." + trimmed;

        // Dedup case-insensitively to match how the scanner compares extensions
        // (OrdinalIgnoreCase, mirroring the case-insensitive filesystems it runs on), so
        // ".command" and ".Command" are one entry rather than two that match the same files.
        if (Extensions.Any(e => string.Equals(e, trimmed, StringComparison.OrdinalIgnoreCase)))
            return false;

        Extensions.Add(trimmed);
        return true;
    }

    public bool AddIgnorePattern(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            return false;

        if (!IsValidRegex(trimmed))
        {
            PatternError = $"Not a valid pattern: {trimmed}";
            return false;
        }

        if (IgnorePatterns.Contains(trimmed))
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
