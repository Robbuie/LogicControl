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
/// usually small; when it is not, the table is still only (changed before) x (changed after).</para>
/// </summary>
public static class LineDiff
{
    public static IReadOnlyList<LineStep> Compute(IReadOnlyList<string> before, IReadOnlyList<string> after, StringComparer? comparer = null)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        comparer ??= StringComparer.Ordinal;

        int head = 0;
        while (head < before.Count && head < after.Count && comparer.Equals(before[head], after[head]))
        {
            head++;
        }

        int tail = 0;
        while (tail < before.Count - head && tail < after.Count - head
            && comparer.Equals(before[before.Count - 1 - tail], after[after.Count - 1 - tail]))
        {
            tail++;
        }

        var steps = new List<LineStep>(Math.Max(before.Count, after.Count));
        for (int i = 0; i < head; i++)
        {
            steps.Add(new LineStep(LineChange.Same, i, i));
        }

        int n = before.Count - head - tail;
        int m = after.Count - head - tail;

        // lcs[i, j]: length of the common subsequence of before[head+i..] and after[head+j..].
        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = comparer.Equals(before[head + i], after[head + j])
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        int x = 0;
        int y = 0;
        while (x < n || y < m)
        {
            if (x < n && y < m && comparer.Equals(before[head + x], after[head + y]))
            {
                steps.Add(new LineStep(LineChange.Same, head + x, head + y));
                x++;
                y++;
            }
            else if (y < m && (x == n || lcs[x, y + 1] >= lcs[x + 1, y]))
            {
                steps.Add(new LineStep(LineChange.Added, -1, head + y));
                y++;
            }
            else
            {
                steps.Add(new LineStep(LineChange.Removed, head + x, -1));
                x++;
            }
        }

        for (int i = 0; i < tail; i++)
        {
            steps.Add(new LineStep(LineChange.Same, before.Count - tail + i, after.Count - tail + i));
        }

        return steps;
    }
}
