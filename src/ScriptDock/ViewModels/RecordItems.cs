using System;
using CommunityToolkit.Mvvm.ComponentModel;
using ScriptDock.Models;
using ScriptDock.Services;

namespace ScriptDock.ViewModels;

/// <summary>
/// One row of the Records list: a summary as stored and the words it shows. The words are rendered when
/// read, so <see cref="Refresh"/>, which the window's view model calls on a language change, re-reads them.
/// </summary>
public sealed class RecordRow : ObservableObject
{
    public RecordRow(RecordSummary summary) => Summary = summary;

    public RecordSummary Summary { get; }
    public string Key => Summary.Key;

    public string TimeText => RecordFormat.Time(Summary.Time);
    public string LevelText => RecordFormat.LevelLabel(Summary.Level);
    public bool IsError => Summary.Level == LogLevel.Error;
    public bool IsWarning => Summary.Level == LogLevel.Warn;
    public string KindText => RecordFormat.KindLabel(Summary.Kind);

    /// <summary>A log line reads as its own kind; the other kinds say what they are.</summary>
    public bool ShowKind => Summary.Kind != RecordKind.Log;

    public string Title => Summary.Kind == RecordKind.ScanReport ? RecordFormat.ScanTitle(Summary.Found) : Summary.Title;

    /// <summary>A message or a path is shown as the app wrote it; a scan's title is a sentence.</summary>
    public bool IsCodeTitle => Summary.Kind != RecordKind.ScanReport;

    public string Text => Summary.Text ?? string.Empty;
    public bool HasText => !string.IsNullOrEmpty(Summary.Text);

    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>One choice of a Records filter. <see cref="Value"/> is null for the choice that turns it off.</summary>
public sealed class RecordFilterOption : ObservableObject
{
    private readonly Func<string> _label;

    public RecordFilterOption(object? value, Func<string> label)
    {
        Value = value;
        _label = label;
    }

    public object? Value { get; }
    public string Label => _label();

    public void Refresh() => OnPropertyChanged(nameof(Label));
}

/// <summary>One stored field of the selected record, under its label.</summary>
public sealed record RecordField(string Label, string Value, bool IsCode);

/// <summary>One stored body of the selected record — a whole event, an output, a report — under its label.</summary>
public sealed record RecordBlock(string Label, string Text);
