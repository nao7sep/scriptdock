using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ScriptDock.Storage;

namespace ScriptDock.Models;

/// <summary>
/// The scripts the user hid from the default list, persisted to <c>~/.scriptdock/hidden.json</c>. Each
/// Hide or Show changes it on its own, apart from the settings Settings edits, so it has its own file
/// (data-lifecycle-conventions); it is the user's choice, so it is protected by the backup history.
/// </summary>
public sealed class HiddenScripts : IJsonNormalizable
{
    /// <summary>The hidden scripts' absolute paths.</summary>
    public List<string> Paths { get; set; } = [];

    /// <summary>A member that is not a path makes the file unreadable, so it is quarantined rather than
    /// silently dropping the user's other hides. A missing list reads as empty.</summary>
    public void NormalizeAfterLoad()
    {
        Paths ??= [];
        if (Paths.Any(path => path is null))
            throw new JsonException("hidden.json holds a member that is not a path.");
    }
}
