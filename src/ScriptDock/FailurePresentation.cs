using System;
using System.Collections.Generic;
using System.Linq;
using ScriptDock.I18n;

namespace ScriptDock;

/// <summary>
/// Maps diagnostic exceptions to deliberately authored ScriptDock presentation copy.
///
/// Each method answers with the key of a sentence, never the sentence: what the reader sees is
/// chosen here, and which language they see it in is decided where it is shown. Diagnostics — the
/// exception's own message — stay out of these entirely, as they always have.
/// </summary>
public static class FailurePresentation
{
    public static Message StartupStorage() => Message.Of("failure.startupStorage");

    /// <summary>The launch stop's notice: one paragraph per refused file, saying whether a newer version wrote it
    /// or it could not be read, then what the reader can do (store-recovery-conventions).</summary>
    public static IReadOnlyList<Message> LaunchStopped(IEnumerable<Storage.RefusedFile> refused) =>
    [
        .. refused.Select(file => Message.Of(file.Newer ? "failure.refusedNewer" : "failure.refusedUnreadable", ("path", file.Path))),
        Message.Of("failure.launchStopped"),
    ];

    /// <summary>How many values a message names before it counts the rest (interface-styling-conventions,
    /// Labels); the file it names holds them all.</summary>
    internal const int NamedValues = 3;

    public static Message KeptSettings(string path, IEnumerable<string> keys)
    {
        var all = keys.ToList();
        var named = string.Join(", ", all.Take(NamedValues));
        object listed = all.Count <= NamedValues
            ? named
            : Message.Of("list.namesAndMore", ("names", named), ("count", all.Count - NamedValues));
        return Message.Of("failure.keptSettings", ("path", path), ("keys", listed));
    }

    public static Message RecordsUnavailable(string path) => Message.Of("failure.recordsUnavailable", ("path", path));

    public static Message BackupsUnavailable(string path) => Message.Of("failure.backupsUnavailable", ("path", path));

    public static Message RootPicker(Exception error) => Message.Of("failure.rootPicker");

    public static Message ScriptStart(Exception error) => error is UnauthorizedAccessException
        ? Message.Of("failure.scriptStartPermission")
        : Message.Of("failure.scriptStart");
}
