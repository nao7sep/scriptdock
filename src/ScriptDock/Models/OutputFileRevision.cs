namespace ScriptDock.Models;

/// <summary>The file extent and filesystem revision copied into the searchable output snapshot.</summary>
public readonly record struct OutputFileRevision(long Length, long LastWriteUtcTicks, long CreationUtcTicks);
