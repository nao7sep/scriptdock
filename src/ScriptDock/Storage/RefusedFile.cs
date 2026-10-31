namespace ScriptDock.Storage;

/// <summary>A file the launch refused: a newer version wrote it, or it could not be read.</summary>
public sealed record RefusedFile(string Path, bool Newer)
{
    /// <summary>The refusal an exception from a store's load or header check stands for, or null when it is not one.</summary>
    public static RefusedFile? From(System.Exception error) => error switch
    {
        NewerFormatVersionException newer => new(newer.FilePath, Newer: true),
        UnreadableStoreException unreadable => new(unreadable.FilePath, Newer: false),
        _ => null,
    };
}
