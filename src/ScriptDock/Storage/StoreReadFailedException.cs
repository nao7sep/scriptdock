using System;
using System.IO;

namespace ScriptDock.Storage;

/// <summary>A protected store could not be read; its content has not been judged malformed.</summary>
public sealed class StoreReadFailedException(string filePath, Exception inner)
    : IOException($"The settings file could not be read: {filePath}", inner)
{
    public string FilePath { get; } = filePath;
}
