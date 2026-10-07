namespace LogicControl.Core.Authoring.History;

/// <summary>How one rung differs between the project (or an older revision) and the draft.</summary>
public enum RungChange
{
    Same,
    Added,
    Changed,
    Removed,
}

/// <summary>
/// One row of a rung-by-rung comparison: the rung's index in each version (-1 where it is not in
/// that version), what happened to it, and whether only its comment differs.
/// </summary>
public readonly record struct RungRow(RungChange Change, int Before, int After, bool CommentChanged);

/// <summary>
/// Lines two rung lists up: rungs whose text is the same are matched, and in each run of
/// differences the removed rungs are paired with the added ones as changed rungs - a person
/// edits a rung rather than deleting it and typing a new one. Text is compared as Studio 5000
/// stores it, so spacing alone is not a change.
/// </summary>
public static class RungComparison
{
    public static IReadOnlyList<RungRow> Compare(IReadOnlyList<RungDraft> before, IReadOnlyList<RungDraft> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        IReadOnlyList<LineStep> steps = LineDiff.Compute(
            before.Select(r => L5xWriter.NormaliseRung(r.Text)).ToList(),
            after.Select(r => L5xWriter.NormaliseRung(r.Text)).ToList());

        var rows = new List<RungRow>(steps.Count);
        for (int i = 0; i < steps.Count;)
        {
            if (steps[i].Change == LineChange.Same)
            {
                int b = steps[i].Before;
                int a = steps[i].After;
                rows.Add(new RungRow(RungChange.Same, b, a, !SameComment(before[b].Comment, after[a].Comment)));
                i++;
                continue;
            }

            var removed = new List<int>();
            var added = new List<int>();
            while (i < steps.Count && steps[i].Change != LineChange.Same)
            {
                if (steps[i].Change == LineChange.Removed)
                {
                    removed.Add(steps[i].Before);
                }
                else
                {
                    added.Add(steps[i].After);
                }

                i++;
            }

            int pairs = Math.Min(removed.Count, added.Count);
            for (int p = 0; p < pairs; p++)
            {
                rows.Add(new RungRow(RungChange.Changed, removed[p], added[p], !SameComment(before[removed[p]].Comment, after[added[p]].Comment)));
            }

            foreach (int b in removed.Skip(pairs))
            {
                rows.Add(new RungRow(RungChange.Removed, b, -1, false));
            }

            foreach (int a in added.Skip(pairs))
            {
                rows.Add(new RungRow(RungChange.Added, -1, a, false));
            }
        }

        return rows;
    }

    public static bool SameComment(string? a, string? b) =>
        string.Equals(string.IsNullOrWhiteSpace(a) ? null : a.Trim(), string.IsNullOrWhiteSpace(b) ? null : b.Trim(), StringComparison.Ordinal);
}
