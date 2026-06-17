using System;
using System.IO;

namespace ScriptDock.Storage;

public static class StorageRoot
{
    private static readonly string DefaultDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".scriptdock");

    public static string Directory { get; private set; } = DefaultDirectory;

    public static string LogsDirectory => Path.Combine(Directory, "logs");

    public static void Override(string directory)
    {
        Directory = directory;
    }

    public static void EnsureExists()
    {
        System.IO.Directory.CreateDirectory(Directory);
    }
}
