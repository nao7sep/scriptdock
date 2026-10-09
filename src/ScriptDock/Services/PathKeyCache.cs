using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace ScriptDock.Services;

/// <summary>
/// <see cref="PathIdentity.Key"/> asks the filesystem for every path, so a slow or unreachable scan folder
/// could stall the window each time its lists are rebuilt. The window reads keys from here instead; each
/// scan refreshes the keys of the paths it found, the hidden list and the Recent list off the interface
/// thread. A path seen for the first time between scans is resolved where it is asked for.
/// </summary>
public sealed class PathKeyCache
{
    private readonly ConcurrentDictionary<string, string> _keys = new(StringComparer.Ordinal);

    public string Key(string path) => _keys.GetOrAdd(path, PathIdentity.Key);

    public bool Same(string left, string right) => PathIdentity.Comparer.Equals(Key(left), Key(right));

    /// <summary>Resolves <paramref name="paths"/> again. Call it off the interface thread.</summary>
    public void Refresh(IEnumerable<string> paths)
    {
        foreach (var path in paths)
            _keys[path] = PathIdentity.Key(path);
    }
}
