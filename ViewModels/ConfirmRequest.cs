namespace ScriptDock.ViewModels;

/// <summary>
/// A destructive-action confirmation the view model asks the view to present (the view owns the
/// dialog). <see cref="ConfirmLabel"/> is the proceed button's text — e.g. "Stop", "Dismiss",
/// "Restart". The handler returns true to proceed, false to cancel.
/// </summary>
public sealed record ConfirmRequest(string Title, string Message, string ConfirmLabel);
