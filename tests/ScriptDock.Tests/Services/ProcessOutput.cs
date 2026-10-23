using System.Collections.Generic;
using System.IO;
using System.Linq;
using ScriptDock.Services;

namespace ScriptDock.Tests.Services;

internal static class ProcessOutput
{
    // Process fixtures observe output through the same feed as the live console.
    public static IReadOnlyList<string> ReadConsoleOutput(this ScriptProcess process)
    {
        if (process.LogFilePath is not { } path || !File.Exists(path)) return [];
        var update = new ConsoleFeed(path).Read();
        return update is null ? [] : update.Completed.Concat(update.Open.Length == 0 ? [] : new[] { update.Open }).ToArray();
    }
}
