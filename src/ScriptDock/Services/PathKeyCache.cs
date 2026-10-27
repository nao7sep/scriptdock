using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace ScriptDock.Services;

/// <summary>
/// <see cref="PathIdentity.Key"/> asks the filesystem for every path, so a slow or unreachable scan folder
/// could stall the window each time its lists are rebuilt. The window reads keys from here instead; each
/// scan refreshes found, known, removed and hidden paths off the interface thread; loading Recent
/// resolves its saved paths there too, before its first rebuild. A path seen for the first time between
/// scans is resolved where it is asked for.
/// </summary>
public sealed class PathKeyCache
{
    private readonly ConcurrentDictionary<string, string> _keys = new(StringComparer.Ordinal);
    private readonly Func<string, string> _resolve;

    public PathKeyCache() : this(PathIdentity.Key) { }
    internal PathKeyCache(Func<string, string> resolve) => _resolve = resolve;

    public string Key(string path) => _keys.GetOrAdd(path, _resolve);

    public bool Same(string left, string right) => PathIdentity.Comparer.Equals(Key(left), Key(right));

    // Preparing a superseded operation must not alter the live projection. Its caller adopts this
    // snapshot only after checking currentness; decisions for that operation use the same prepared keys.
    public IReadOnlyDictionary<string, string> Prepare(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        var prepared = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!prepared.ContainsKey(path)) prepared[path] = _resolve(path);
        }
        return prepared;
    }

    public void Adopt(IReadOnlyDictionary<string, string> prepared)
    {
        foreach (var (path, key) in prepared) _keys[path] = key;
    }

    // Startup Recent only warms missing identities; a scan accepted while this read was pending
    // already owns the current projection and its keys must survive the older preparation.
    public void Seed(IReadOnlyDictionary<string, string> prepared)
    {
        foreach (var (path, key) in prepared) _keys.TryAdd(path, key);
    }
}
