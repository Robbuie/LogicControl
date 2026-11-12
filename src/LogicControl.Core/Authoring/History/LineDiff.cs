namespace LogicControl.Core.Authoring.History;

/// <summary>What happened to one line between two versions of a list.</summary>
public enum LineChange
{
    Same,
    Added,
    Removed,
}

/// <summary>One step of a line diff: the line's index in each version (-1 where it is absent).</summary>
public readonly record struct LineStep(LineChange Change, int Before, int After);

/// <summary>
/// The longest-common-subsequence diff, over rungs or members or any list of strings.
///
/// <para>The common head and tail are trimmed first, so editing one rung of a 2,000-rung
/// routine compares one rung, not four million pairs. What is left in the middle is
/// usually small. When it is bigger than <see cref="MaxTable"/> cells - two routines that differ
/// all through - lines unique to each side anchor it and the gaps are diffed one by one, so memory
/// stays bounded where the table would have needed tens of megabytes.</para>
/// </summary>
public static class LineDiff
{
    /// <summary>
    /// The largest table the exact diff fills - (changed before) x (changed after) cells, four bytes
    /// each. Past it the lines found once on each side anchor the diff and the gaps between them
    /// are diffed on their own; with no anchors at all, the middle reads as removed then added.
    /// </summary>
    internal const long MaxTable = 1_000_000;

    public static IReadOnlyList<LineStep> Compute(IReadOnlyList<string> before, IReadOnlyList<string> after, StringComparer? comparer = null)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        comparer ??= StringComparer.Ordinal;

        var steps = new List<LineStep>(Math.Max(before.Count, after.Count));
        Diff(before, after, comparer, 0, before.Count, 0, after.Count, steps);
        return steps;
    }

    /// <summary>Diffs before[b0..b1) against after[a0..a1) onto <paramref name="steps"/>.</summary>
    private static void Diff(
        IReadOnlyList<string> before, IReadOnlyList<string> after, StringComparer comparer,
        int b0, int b1, int a0, int a1, List<LineStep> steps)
    {
        // The common head and tail are trimmed first, so editing one rung of a 2,000-rung routine
        // compares one rung, not four million pairs.
        int head = 0;
        while (b0 + head < b1 && a0 + head < a1 && comparer.Equals(before[b0 + head], after[a0 + head]))
        {
            steps.Add(new LineStep(LineChange.Same, b0 + head, a0 + head));
            head++;
        }

        b0 += head;
        a0 += head;
        int tail = 0;
        while (b1 - tail > b0 && a1 - tail > a0 && comparer.Equals(before[b1 - 1 - tail], after[a1 - 1 - tail]))
        {
            tail++;
        }

        int n = b1 - tail - b0;
        int m = a1 - tail - a0;
        if ((long)n * m <= MaxTable)
        {
            Exact(before, after, comparer, b0, n, a0, m, steps);
        }
        else
        {
            Anchored(before, after, comparer, b0, b0 + n, a0, a0 + m, steps);
        }

        for (int i = tail; i > 0; i--)
        {
            steps.Add(new LineStep(LineChange.Same, b1 - i, a1 - i));
        }
    }

    /// <summary>The longest-common-subsequence diff of a middle small enough for its table.</summary>
    private static void Exact(
        IReadOnlyList<string> before, IReadOnlyList<string> after, StringComparer comparer,
        int b0, int n, int a0, int m, List<LineStep> steps)
    {
        // lcs[i, j]: length of the common subsequence of before[b0+i..] and after[a0+j..].
        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = comparer.Equals(before[b0 + i], after[a0 + j])
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        int x = 0;
        int y = 0;
        while (x < n || y < m)
        {
            if (x < n && y < m && comparer.Equals(before[b0 + x], after[a0 + y]))
            {
                steps.Add(new LineStep(LineChange.Same, b0 + x, a0 + y));
                x++;
                y++;
            }
            else if (y < m && (x == n || lcs[x, y + 1] >= lcs[x + 1, y]))
            {
                steps.Add(new LineStep(LineChange.Added, -1, a0 + y));
                y++;
            }
            else
            {
                steps.Add(new LineStep(LineChange.Removed, b0 + x, -1));
                x++;
            }
        }
    }

    /// <summary>
    /// A middle too big for the table: the lines that appear exactly once on each side, kept in
    /// the longest run that is in the same order on both (patience diff), are matched, and each
    /// gap between them is diffed on its own.
    /// </summary>
    private static void Anchored(
        IReadOnlyList<string> before, IReadOnlyList<string> after, StringComparer comparer,
        int b0, int b1, int a0, int a1, List<LineStep> steps)
    {
        List<(int B, int A)> anchors = Anchors(before, after, comparer, b0, b1, a0, a1);
        if (anchors.Count == 0)
        {
            for (int i = b0; i < b1; i++)
            {
                steps.Add(new LineStep(LineChange.Removed, i, -1));
            }

            for (int j = a0; j < a1; j++)
            {
                steps.Add(new LineStep(LineChange.Added, -1, j));
            }

            return;
        }

        int b = b0;
        int a = a0;
        foreach ((int ab, int aa) in anchors)
        {
            Diff(before, after, comparer, b, ab, a, aa, steps);
            steps.Add(new LineStep(LineChange.Same, ab, aa));
            b = ab + 1;
            a = aa + 1;
        }

        Diff(before, after, comparer, b, b1, a, a1, steps);
    }

    private static List<(int B, int A)> Anchors(
        IReadOnlyList<string> before, IReadOnlyList<string> after, StringComparer comparer,
        int b0, int b1, int a0, int a1)
    {
        var counts = new Dictionary<string, (int InBefore, int InAfter, int B, int A)>(comparer);
        for (int i = b0; i < b1; i++)
        {
            counts.TryGetValue(before[i], out var c);
            counts[before[i]] = (c.InBefore + 1, c.InAfter, i, c.A);
        }

        for (int j = a0; j < a1; j++)
        {
            counts.TryGetValue(after[j], out var c);
            counts[after[j]] = (c.InBefore, c.InAfter + 1, c.B, j);
        }

        List<(int B, int A)> unique = counts.Values
            .Where(c => c.InBefore == 1 && c.InAfter == 1)
            .Select(c => (c.B, c.A))
            .OrderBy(p => p.B)
            .ToList();

        // Longest increasing run of A over B order - patience sorting, n log n.
        var tails = new List<int>();
        var previous = new int[unique.Count];
        for (int k = 0; k < unique.Count; k++)
        {
            int lo = 0;
            int hi = tails.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (unique[tails[mid]].A < unique[k].A)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            previous[k] = lo > 0 ? tails[lo - 1] : -1;
            if (lo == tails.Count)
            {
                tails.Add(k);
            }
            else
            {
                tails[lo] = k;
            }
        }

        var run = new List<(int B, int A)>(tails.Count);
        for (int k = tails.Count == 0 ? -1 : tails[^1]; k >= 0; k = previous[k])
        {
            run.Add(unique[k]);
        }

        run.Reverse();
        return run;
    }
}
