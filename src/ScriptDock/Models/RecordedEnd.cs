namespace ScriptDock.Models;

/// <summary>
/// How a run ended, as its end was recorded: the state the records hold (<c>exited</c>, <c>terminated</c>
/// or <c>failed</c>) and the exit code when the OS gave one.
/// </summary>
public sealed record RecordedEnd(string State, int? ExitCode);
