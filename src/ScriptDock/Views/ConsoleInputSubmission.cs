namespace ScriptDock.Views;

/// <summary>Owns one in-flight console submission without losing text typed while it awaits I/O.</summary>
internal sealed class ConsoleInputSubmission
{
    private bool _pending;

    public bool TryBegin(string text, out string snapshot)
    {
        snapshot = text;
        if (_pending)
            return false;
        _pending = true;
        return true;
    }

    public string Complete(string snapshot, bool sent, string currentText)
    {
        _pending = false;
        if (sent)
            return currentText;

        // Keep the failed line ahead of anything typed while it was pending. A newline preserves
        // both logical submissions exactly; the next successful WriteLine sends them in order.
        return currentText.Length == 0 ? snapshot : snapshot + "\n" + currentText;
    }
}
