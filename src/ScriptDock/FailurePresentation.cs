using System;
using System.Collections.Generic;
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

    public static Message StartupData(string path) => Message.Of("failure.startupData", ("path", path));

    public static Message RecoveredData(string path) => Message.Of("failure.recoveredData", ("path", path));

    public static Message NewerStore(string path) => Message.Of("failure.newerStore", ("path", path));

    public static Message KeptSettings(string path, IEnumerable<string> keys) =>
        Message.Of("failure.keptSettings", ("path", path), ("keys", string.Join(", ", keys)));

    public static Message RecordsUnavailable(string path) => Message.Of("failure.recordsUnavailable", ("path", path));

    public static Message RootPicker(Exception error) => Message.Of("failure.rootPicker");

    public static Message ScriptStart(Exception error) => error is UnauthorizedAccessException
        ? Message.Of("failure.scriptStartPermission")
        : Message.Of("failure.scriptStart");
}
