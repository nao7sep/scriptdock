using System;

namespace ScriptDock.Storage;

/// <summary>
/// A store records a format version newer than this build reads. The file is intact data, not a
/// corrupt one: it is left exactly in place and reported by name (store-recovery-conventions).
/// </summary>
public sealed class NewerFormatVersionException(string filePath, int found, int supported)
    : Exception($"{filePath} has format version {found}; this build reads up to {supported}.")
{
    public string FilePath { get; } = filePath;

    public int Found { get; } = found;

    public int Supported { get; } = supported;
}
