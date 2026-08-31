namespace ScriptDock.ViewModels;

/// <summary>
/// Presentation of the ordinary status-bar activity line: <see cref="Info"/> reads secondary and
/// <see cref="Busy"/> uses the accent while an operation is in flight. Actionable failures do not
/// belong to this replaceable slot; MainWindowViewModel keeps them in its persistent error surface.
/// </summary>
public enum StatusKind
{
    Info,
    Busy,
}
