using System;
using System.IO;

namespace ScriptDock.Storage;

/// <summary>A store's file exists but could not be read or interpreted: it could not be opened, will not
/// parse, or has no valid header. It is left exactly as it is (store-recovery-conventions).</summary>
public sealed class UnreadableStoreException(string filePath, Exception inner)
    : IOException($"{filePath} could not be read.", inner)
{
    public string FilePath { get; } = filePath;
}
