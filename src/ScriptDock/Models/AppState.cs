namespace ScriptDock.Models;

/// <summary>
/// View state, persisted to <c>~/.scriptdock/state.json</c>: regenerable UI state that should not
/// churn the durable preferences in <see cref="AppConfig"/>.
/// </summary>
public sealed class AppState
{
    /// <summary>Whether hidden scripts are currently shown.</summary>
    public bool ShowHidden { get; set; }

    /// <summary>Persisted width of the Recent pane (the resizable right column); null until first saved.</summary>
    public double? RecentPaneWidth { get; set; }

    /// <summary>Persisted height of the Console pane; null until first saved.</summary>
    public double? ConsoleHeight { get; set; }

    public int? WindowPositionX { get; set; }
    public int? WindowPositionY { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    /// <summary>Persisted width of the Records window's list pane; null until first dragged.</summary>
    public double? RecordsListWidth { get; set; }

    public int? RecordsWindowPositionX { get; set; }
    public int? RecordsWindowPositionY { get; set; }
    public double? RecordsWindowWidth { get; set; }
    public double? RecordsWindowHeight { get; set; }
    public bool RecordsWindowMaximized { get; set; }
}
