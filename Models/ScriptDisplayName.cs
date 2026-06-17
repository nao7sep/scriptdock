using System;

namespace ScriptDock.Models;

/// <summary>
/// Computes the compact <c>app/script</c> label for a script path (the conventional
/// <c>&lt;app&gt;/scripts/&lt;name&gt;</c> layout), falling back to the file name. Shared by every
/// list row so the display is identical everywhere.
/// </summary>
public static class ScriptDisplayName
{
    public static string For(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        var dir = System.IO.Path.GetDirectoryName(path);
        if (dir is null)
            return name;

        if (string.Equals(System.IO.Path.GetFileName(dir), "scripts", StringComparison.OrdinalIgnoreCase))
        {
            var app = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(dir) ?? string.Empty);
            if (!string.IsNullOrEmpty(app))
                return $"{app}/{name}";
        }

        return name;
    }
}
