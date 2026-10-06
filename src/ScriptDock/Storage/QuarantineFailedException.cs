using System;

namespace ScriptDock.Storage;

/// <summary>
/// An unreadable store could not be moved aside, so it was left exactly in place and startup halts
/// rather than let defaults overwrite it; the halt names the file (store-recovery-conventions).
/// </summary>
public sealed class QuarantineFailedException(string filePath, Exception moveError)
    : Exception($"{filePath} is unreadable and could not be moved aside.", moveError)
{
    public string FilePath { get; } = filePath;
}
