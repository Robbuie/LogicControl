using System.Globalization;
using System.Text.Json.Serialization;

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

    /// <summary>
    /// The drafts after this step: the name of the top piece of their snapshot in the set's
    /// <see cref="RevisionStore"/>. Two revisions with the same drafts have the same root.
    /// </summary>
    public string? Root { get; set; }

    /// <summary>
    /// The whole snapshot as text - how a revision was stored before 0.5.6. Read from older files
    /// and turned into a <see cref="Root"/> when the history is opened; never written.
    /// </summary>
    public string? Snapshot { get; set; }

    /// <summary>The number this one put the drafts back to, when it is a revert.</summary>
    public int? RevertedTo { get; set; }

    /// <summary>The store <see cref="Root"/> names a piece in - set when the history is opened or the revision recorded.</summary>
    [JsonIgnore]
    internal RevisionStore? Store { get; set; }
}

/// <summary>
/// Every step of the work on a development set, each one a full snapshot, so any of them can be
/// looked at, compared with any other, or gone back to.
///
/// <para><b>Snapshots, not edits.</b> A revision is the whole set as it stood, not the change
/// that made it, so going back to step 12 is reading step 12 - no replaying, nothing that one bad
/// step can break for every step after it. What changed is worked out on demand by comparing a
/// snapshot with the one before (<see cref="SetDiff"/>).</para>
///
/// <para><b>Stored in shared pieces.</b> A routine edited in place is the whole routine, so a
/// snapshot can be hundreds of kilobytes. Each one is cut into pieces in the set's
/// <see cref="RevisionStore"/> (<see cref="DevelopmentSet.HistoryStore"/>) and a piece an earlier
/// revision holds is not stored again: a revision that changed one rung costs about the chunk of
/// rungs around it, not another copy of the routine.</para>
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

    /// <summary>Once revisions are being dropped, unused pieces are cleared out every this many revisions.</summary>
    internal const int PruneEvery = 50;

    private readonly List<RevisionRecord> _records;
    private readonly RevisionStore _store;

    /// <summary>The latest revision's snapshot as text, once known: "nothing changed" without cutting it up, and its drafts without putting them back together.</summary>
    private (string Root, string Snapshot)? _latest;

    /// <summary>
    /// The history stored in <paramref name="set"/> - started empty if it has none. Revisions read
    /// from a file saved before 0.5.6, each holding its whole snapshot, are moved into the store.
    /// </summary>
    public RevisionHistory(DevelopmentSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        set.History ??= [];
        _records = set.History;

        // A history handed from one set to another (a revert, clearing the drafts) keeps its store:
        // the revisions know which one they were recorded in.
        RevisionStore? known = _records.Select(r => r.Store).FirstOrDefault(s => s is not null);
        set.HistoryStore ??= known?.Pieces ?? new Dictionary<string, string>(StringComparer.Ordinal);
        _store = known is not null && ReferenceEquals(known.Pieces, set.HistoryStore) ? known : new RevisionStore(set.HistoryStore);

        bool moved = false;
        foreach (RevisionRecord r in _records)
        {
            if (r.Store is { } other && !ReferenceEquals(other.Pieces, _store.Pieces))
            {
                foreach (KeyValuePair<string, string> piece in other.Pieces)
                {
                    _store.Pieces.TryAdd(piece.Key, piece.Value);
                }
            }

            r.Store = _store;
            if (r.Root is null && !string.IsNullOrEmpty(r.Snapshot))
            {
                r.Root = _store.Put(r.Snapshot);
                r.Snapshot = null;
                moved = true;
            }
        }

        if (moved)
        {
            Prune();
        }
    }

    /// <summary>The pieces the revisions are made of.</summary>
    public RevisionStore Store => _store;

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
        string? latestSnapshot = latest is null ? null : SnapshotOf(latest);
        if (string.Equals(latestSnapshot, snapshot, StringComparison.Ordinal))
        {
            return null;
        }

        string root = _store.Put(snapshot);
        if (latest is not null && string.Equals(latest.Root, root, StringComparison.Ordinal))
        {
            return null;
        }

        ChangeSet changes = latestSnapshot is null
            ? SetDiff.Compare(new DevelopmentSet { ControllerName = current.ControllerName, SoftwareRevision = current.SoftwareRevision }, current)
            : SetDiff.Compare(DevelopmentSet.FromSnapshot(latestSnapshot), current);

        var record = new RevisionRecord
        {
            Number = (latest?.Number ?? 0) + 1,
            TimeUtc = now ?? DateTime.UtcNow,
            Author = author,
            Label = label,
            Summary = changes.Summary,
            Root = root,
            RevertedTo = revertedTo,
            Store = _store,
        };

        _records.Add(record);
        _latest = (root, snapshot);
        if (_records.Count > Limit)
        {
            _records.RemoveRange(0, _records.Count - Limit);
            if (record.Number % PruneEvery == 0)
            {
                Prune();
            }
        }

        return record;
    }

    private string SnapshotOf(RevisionRecord revision)
    {
        if (revision.Root is not null && _latest is { } known && string.Equals(known.Root, revision.Root, StringComparison.Ordinal))
        {
            return known.Snapshot;
        }

        string snapshot = revision.Root is null ? revision.Snapshot ?? string.Empty : _store.Get(revision.Root);
        if (revision.Root is not null)
        {
            _latest = (revision.Root, snapshot);
        }

        return snapshot;
    }

    /// <summary>Removes the pieces no revision uses any more; returns how many went.</summary>
    internal int Prune() => _store.Prune(_records.Select(r => r.Root).OfType<string>());

    public RevisionRecord? Find(int number) => _records.FirstOrDefault(r => r.Number == number);

    /// <summary>The drafts as they stood after <paramref name="revision"/> - a fresh copy to work on.</summary>
    public static DevelopmentSet Restore(RevisionRecord revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        if (revision.Root is null)
        {
            return DevelopmentSet.FromSnapshot(revision.Snapshot ?? throw new InvalidDataException($"Revision {revision.Number} holds no drafts."));
        }

        RevisionStore store = revision.Store
            ?? throw new InvalidOperationException("The revision has not been opened through a RevisionHistory.");
        return DevelopmentSet.FromSnapshot(store.Get(revision.Root));
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
