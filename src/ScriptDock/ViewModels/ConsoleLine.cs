namespace ScriptDock.ViewModels;

/// <summary>
/// One line of the live console. <see cref="Shown"/> is the line as drawn: a line longer than the console
/// draws is shortened with one marker, as the Records detail does, because laying out one very long line
/// costs far more than its length; <see cref="Text"/> stays whole for copying, and the run's whole output
/// opens from the console (volume-handling-conventions, Output and text length).
/// </summary>
public sealed class ConsoleLine(string text)
{
    public string Text { get; } = text;

    public string Shown { get; } = Shorten(text);

    // As the Records detail draws a line: whole up to its length, else its beginning and one marker.
    private static string Shorten(string text)
    {
        if (text.Length <= RecordFormat.OutputShownLineGraphemes)
            return text;
        var (kept, truncated) = TextCleanup.Truncate(text, RecordFormat.OutputShownLineGraphemes);
        return truncated ? kept + "…" : text;
    }
}
