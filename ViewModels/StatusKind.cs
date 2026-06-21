namespace ScriptDock.ViewModels;

/// <summary>
/// Severity of the status-bar message, which drives its text colour
/// (see <c>MainWindowViewModel.StatusBrush</c>): <see cref="Info"/> reads secondary, <see cref="Busy"/>
/// uses the accent while an operation is in flight, and <see cref="Error"/> uses the danger colour so a
/// failure can't blend into a normal line.
/// </summary>
public enum StatusKind
{
    Info,
    Busy,
    Error,
}
