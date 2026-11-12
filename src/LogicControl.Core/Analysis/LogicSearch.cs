using LogicControl.Core.Logic;
using LogicControl.Core.Model;

namespace LogicControl.Core.Analysis;

/// <summary>One place a search found: where, what kind of text, and that text.</summary>
public sealed record SearchHit(FindingSite Site, SearchHitKind Kind, string Text)
{
    public string Where => Site.Text;
}

public enum SearchHitKind
{
    /// <summary>A rung's logic, or a line of structured text.</summary>
    Logic,

    /// <summary>A rung's comment.</summary>
    Comment,
}

/// <summary>
/// Finds text across every routine of a project - rungs, rung comments and structured text - for
/// the Search tab: "where else is Conveyor_Run used", "which rungs mention the jam timer".
///
/// <para>As text, the search is a plain substring, ignoring case. As a tag, it matches an operand
/// whose tag name is the one asked for - <c>Motor</c> finds <c>Motor.Run</c> and <c>Motor[3]</c>
/// but not <c>Motor2</c> - in ladder operands and in structured text, the way the cross-reference
/// reads them, and does not look in comments. Results come in routine order, capped at
/// <see cref="Limit"/> so a search for "XIC" in a big plant answers at once.</para>
/// </summary>
public static class LogicSearch
{
    public const int Limit = 2000;

    public static IReadOnlyList<SearchHit> Find(PlcProject project, string query, bool wholeTag = false, int limit = Limit)
    {
        ArgumentNullException.ThrowIfNull(project);
        string q = (query ?? string.Empty).Trim();
        var hits = new List<SearchHit>();
        if (q.Length == 0)
        {
            return hits;
        }

        foreach (RoutineInfo routine in project.AllRoutines)
        {
            if (routine.Language == RoutineLanguage.StructuredText)
            {
                for (int line = 0; line < routine.StructuredText.Count && hits.Count < limit; line++)
                {
                    string text = routine.StructuredText[line];
                    if (wholeTag ? MentionsTag(text, q, structuredText: true) : Contains(text, q))
                    {
                        hits.Add(new SearchHit(new FindingSite(routine.QualifiedName, line, IsStructuredText: true), SearchHitKind.Logic, text.Trim()));
                    }
                }

                continue;
            }

            foreach (RungInfo rung in routine.Rungs)
            {
                if (hits.Count >= limit)
                {
                    return hits;
                }

                var site = new FindingSite(routine.QualifiedName, rung.Number);
                if (wholeTag ? MentionsTag(rung.Text, q, structuredText: false) : Contains(rung.Text, q))
                {
                    hits.Add(new SearchHit(site, SearchHitKind.Logic, rung.Text));
                }
                else if (!wholeTag && rung.Comment is { } comment && Contains(comment, q))
                {
                    hits.Add(new SearchHit(site, SearchHitKind.Comment, comment.Trim()));
                }
            }
        }

        return hits;
    }

    private static bool Contains(string? text, string q) =>
        text is not null && text.Contains(q, StringComparison.OrdinalIgnoreCase);

    private static bool MentionsTag(string? text, string tag, bool structuredText)
    {
        if (text is null || !text.Contains(tag, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        IEnumerable<string> operands = structuredText
            ? [text]
            : RungParser.Parse(text).SelectMany(i => i.Operands);
        return operands.SelectMany(TagReference.BaseNames)
            .Any(name => string.Equals(name, tag, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Unscoped(name), tag, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"Program:Main.Motor" read as "Motor" - a tag asked for by its own name.</summary>
    private static string Unscoped(string name)
    {
        int dot = name.StartsWith("Program:", StringComparison.OrdinalIgnoreCase) ? name.IndexOf('.', StringComparison.Ordinal) : -1;
        return dot > 0 ? name[(dot + 1)..] : name;
    }
}
