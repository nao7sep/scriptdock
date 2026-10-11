using System.Collections.Generic;
using ScriptDock.Models;

namespace ScriptDock.ViewModels;

/// <summary>The cursor of a successfully loaded page.</summary>
public static class RecordsPaging
{
    /// <summary>The page after the last record shown, or null when nothing is shown.</summary>
    public static RecordCursor? CursorAfter(IReadOnlyList<RecordSummary> records) =>
        records.Count == 0 ? null : new RecordCursor(records[^1].Time, records[^1].Kind, records[^1].Id);
}
