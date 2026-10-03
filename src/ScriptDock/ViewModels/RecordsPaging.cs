using System;
using System.Collections.Generic;
using System.Linq;
using ScriptDock.Models;

namespace ScriptDock.ViewModels;

/// <summary>The Records list's paging decisions: where the next page starts, the order rows are shown in,
/// and how a re-read newest page joins the rows already shown.</summary>
public static class RecordsPaging
{
    /// <summary>The page after the last record shown, or null when nothing is shown.</summary>
    public static RecordCursor? CursorAfter(IReadOnlyList<RecordSummary> records) =>
        records.Count == 0 ? null : new RecordCursor(records[^1].Time, records[^1].Kind, records[^1].Id);

    /// <summary>Newest first, as the database pages them: by time, then kind name, then id, each descending.
    /// Stored times are ISO text of one shape, so they compare as text.</summary>
    public static int NewestFirst(RecordSummary a, RecordSummary b)
    {
        var time = string.CompareOrdinal(b.Time, a.Time);
        if (time != 0)
            return time;
        var kind = string.CompareOrdinal(RecordKinds.Name(b.Kind), RecordKinds.Name(a.Kind));
        return kind != 0 ? kind : b.Id.CompareTo(a.Id);
    }

    /// <summary>
    /// The newest page read again, joined with the rows already shown: a row in both takes the page's copy,
    /// and the rows shown beyond the page stay, so the pages already read are kept. Whether more can be read
    /// stays as it was when rows beyond the page are shown, and is the page's otherwise.
    /// </summary>
    public static (IReadOnlyList<RecordSummary> Records, bool More) MergeNewestPage(
        IReadOnlyList<RecordSummary> shown, bool shownMore, RecordsPage page)
    {
        var byKey = new Dictionary<string, RecordSummary>(StringComparer.Ordinal);
        foreach (var record in shown)
            byKey[record.Key] = record;
        foreach (var record in page.Records)
            byKey[record.Key] = record;

        var records = byKey.Values.ToList();
        records.Sort(NewestFirst);
        var last = page.Records.Count == 0 ? null : page.Records[^1];
        var beyond = last is not null && shown.Any(record => NewestFirst(record, last) > 0);
        return (records, beyond ? shownMore : page.More);
    }
}
