namespace ScriptDock.Models;

/// <summary>Persisted main-window geometry so the window reopens where it was left.</summary>
public sealed class WindowBounds
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}
