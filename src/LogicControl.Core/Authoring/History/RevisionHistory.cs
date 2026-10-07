using System.Globalization;

namespace LogicControl.Core.Authoring.History;

/// <summary>Who made a revision - shown in the history list and kept in the .lcdev file.</summary>
public static class RevisionAuthor
{
    public const string You = "You";
    public const string Claude = "Claude";
    public const string Template = "Template";
    public const string File = "File";
}

/// <summary>
/// One saved step: when, by whom, what it was, and the drafts exactly as they stood after it.
/// The stored form in a .lcdev file; plain properties so System.Text.Json reads and writes it.
/// </summary>
public sealed class RevisionRecord
{
    /// <summary>1, 2, 3... in the order they were made. Never reused, even after a revert.</summary>
    public int Number { get; set; }

    public DateTime TimeUtc { get; set; }

    public string Author { get; set; } = RevisionAuthor.You;

    /// <summary>What was done: "Edited Routine MainProgram/Motors", "Generated motor starters".</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>One line on what changed, worked out when it was recorded.</summary>
    public string? Summary { get; set; }

    /// <summary>The drafts after this step - <see cref="DevelopmentSet.ToSnapshot"/>.</summary>
    public string Snapshot { get; set; } = string.Empty;

    /// <summary>The number this one put the drafts back to, when it is a revert.</summary>
    public int? RevertedTo { get; set; }
}

/// <summary>
/// Every step of the work on a development set, each one a full snapshot, so any of them can be
/// looked at, compared with any other, or gone back to.
///
/// <para><b>Snapshots, not edits.</b> A revision stores the whole set as it stood, not the change
/// that made it. A set is tens of drafts, so a snapshot is a few kilobytes, and storing them
/// whole means going back to step 12 is reading step 12 - no replaying, nothing that one bad
/// step can break for every step after it. What changed is worked out on demand by comparing a
/// snapshot with the one before (<see cref="SetDiff"/>).</para>
///
/// <para><b>Going back is a step forward.</b> Reverting to revision 12 records a new revision
/// whose drafts are revision 12's. Nothing is thrown away, so a revert can itself be undone by
/// reverting to the revision before it.</para>
///
/// <para>The list is the <see cref="DevelopmentSet.History"/> of the set it belongs to, so saving
/// the set saves the history. Past <see cref="Limit"/> revisions the oldest are dropped.</para>
/// </summary>
public sealed class RevisionHistory
{
    /// <summary>The most revisions a set keeps. A long day of edits is a few hundred.</summary>
    public const int Limit = 500;

    private readonly List<RevisionRecord> _records;

    /// <summary>The history stored in <paramref name="set"/> - started empty if it has none.</summary>
    public RevisionHistory(DevelopmentSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        set.History ??= [];
        _records = set.History;
    }

    /// <summary>Oldest first.</summary>
    public IReadOnlyList<RevisionRecord> Revisions => _records;

    public RevisionRecord? Latest => _records.Count == 0 ? null : _records[^1];

    /// <summary>
    /// Records the set as it stands now, unless it is exactly what the latest revision already
    /// holds. Returns the new revision, or null when nothing had changed.
    /// </summary>
    public RevisionRecord? Record(DevelopmentSet current, string label, string author = RevisionAuthor.You, DateTime? now = null, int? revertedTo = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        string snapshot = current.ToSnapshot();
        RevisionRecord? latest = Latest;
        if (latest is not null && string.Equals(latest.Snapshot, snapshot, StringComparison.Ordinal))
        {
            return null;
        }

        ChangeSet changes = latest is null
            ? SetDiff.Compare(new DevelopmentSet { ControllerName = current.ControllerName, SoftwareRevision = current.SoftwareRevision }, current)
            : SetDiff.Compare(DevelopmentSet.FromSnapshot(latest.Snapshot), current);

        var record = new RevisionRecord
        {
            Number = (latest?.Number ?? 0) + 1,
            TimeUtc = now ?? DateTime.UtcNow,
            Author = author,
            Label = label,
            Summary = changes.Summary,
            Snapshot = snapshot,
            RevertedTo = revertedTo,
        };

        _records.Add(record);
        if (_records.Count > Limit)
        {
            _records.RemoveRange(0, _records.Count - Limit);
        }

        return record;
    }

    public RevisionRecord? Find(int number) => _records.FirstOrDefault(r => r.Number == number);

    /// <summary>The drafts as they stood after <paramref name="revision"/> - a fresh copy to work on.</summary>
    public static DevelopmentSet Restore(RevisionRecord revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        return DevelopmentSet.FromSnapshot(revision.Snapshot);
    }

    /// <summary>What <paramref name="revision"/> changed: it compared with the one before it.</summary>
    public ChangeSet ChangesIn(RevisionRecord revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        int at = _records.IndexOf(revision);
        DevelopmentSet after = Restore(revision);
        DevelopmentSet before = at > 0
            ? Restore(_records[at - 1])
            : new DevelopmentSet { ControllerName = after.ControllerName, SoftwareRevision = after.SoftwareRevision };
        return SetDiff.Compare(before, after);
    }

    /// <summary>What differs between <paramref name="revision"/> and <paramref name="current"/> - "what reverting would undo".</summary>
    public static ChangeSet Since(RevisionRecord revision, DevelopmentSet current)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(current);
        return SetDiff.Compare(Restore(revision), current);
    }

    /// <summary>"#12 · 14:05 · You · Edited Routine MainProgram/Motors" - for a log or a tooltip.</summary>
    public static string Describe(RevisionRecord r) =>
        $"#{r.Number.ToString(CultureInfo.InvariantCulture)} · {r.TimeUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)} · {r.Author} · {r.Label}";
}
