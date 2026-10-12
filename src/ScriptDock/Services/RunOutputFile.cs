using System;
using System.IO;
using System.Text;

namespace ScriptDock.Services;

/// <summary>
/// Writes a run's whole stored output to a text file the user's editor can open. The Records detail shows
/// only a bounded prefix, so this is the direct path to the complete value (text-cleanup conventions,
/// rule 4). The file is a disposable view rebuilt from the record on every open: it lives under the storage
/// root in <c>output-views/</c>, one file per run, and a session's first write clears the files earlier
/// sessions left, so the folder never holds more than one session's opened outputs.
/// </summary>
public static class RunOutputFile
{
    public const string DirectoryName = "output-views";

    private static readonly object Gate = new();
    private static string? _clearedDirectory;

    /// <summary>Writes <paramref name="output"/> as the console shows it (<see cref="RunLog.Lines"/>), whole,
    /// and returns the file's path. Throws when the file cannot be written.</summary>
    public static string Write(string root, string script, long id, byte[] output)
    {
        var directory = Path.Combine(root, DirectoryName);
        var stem = Path.GetFileNameWithoutExtension(script) is { Length: > 0 } name ? name : "output";
        var path = Path.Combine(directory, $"{stem}-{id}.txt");
        var temp = Path.Combine(directory, $"{stem}-{id}-{NanoId.New()}.tmp");
        var text = string.Join("\n", RunLog.Lines(Encoding.UTF8.GetString(output)));

        lock (Gate)
        {
            if (_clearedDirectory != directory)
            {
                Clear(directory);
                _clearedDirectory = directory;
            }
            Directory.CreateDirectory(directory);
        }

        // Published whole by a rename, so an editor never opens a half-written file; a reopen replaces it.
        try
        {
            File.WriteAllText(temp, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        return path;
    }

    // Best effort: a file an editor still holds (Windows) stays until a later session clears it.
    private static void Clear(string directory)
    {
        if (!Directory.Exists(directory))
            return;

        foreach (var file in Directory.EnumerateFiles(directory))
        {
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Debug("output view: could not clear", new { file, error = ex.Message });
            }
        }
    }
}
