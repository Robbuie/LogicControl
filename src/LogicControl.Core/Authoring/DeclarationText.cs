using System.Globalization;

namespace LogicControl.Core.Authoring;

/// <summary>
/// Reads and writes declarations as lines of text, for pasting a member list or a tag list out of
/// a spreadsheet or an I/O list instead of typing it into a grid row by row.
///
/// <para>Each line is a name, a type and an optional description, in any of the shapes people
/// actually have them in:</para>
/// <code>
/// Speed : REAL // Commanded speed
/// Speed  REAL[4]  Commanded speed
/// Speed,REAL,Commanded speed         (CSV, or tab-separated from Excel)
/// </code>
/// <para>Blank lines and lines starting with // or # are skipped. A line that cannot be read is
/// reported with its number rather than skipped silently.</para>
/// </summary>
public static class DeclarationText
{
    public static (List<MemberDraft> Members, List<string> Problems) ParseMembers(string? text)
    {
        var members = new List<MemberDraft>();
        var problems = new List<string>();

        foreach ((int line, string name, string type, string? dims, string? description) in Lines(text, problems))
        {
            int dimension = 0;
            if (dims is not null && (dims.Contains(' ', StringComparison.Ordinal) || !int.TryParse(dims, NumberStyles.None, CultureInfo.InvariantCulture, out dimension)))
            {
                problems.Add($"Line {line}: a UDT member can have one dimension; '{dims}' is not one.");
                continue;
            }

            members.Add(new MemberDraft(name, LogixTypes.Canonical(type), description, dimension));
        }

        return (members, problems);
    }

    public static (List<TagDraft> Tags, List<string> Problems) ParseTags(string? text, string? program)
    {
        var tags = new List<TagDraft>();
        var problems = new List<string>();

        foreach ((_, string name, string type, string? dims, string? description) in Lines(text, problems))
        {
            tags.Add(new TagDraft(name, LogixTypes.Canonical(type), description, program) { Dimensions = dims });
        }

        return (tags, problems);
    }

    public static string FormatMembers(IEnumerable<MemberDraft> members) =>
        string.Join(Environment.NewLine, members.Select(m =>
            $"{m.Name} : {m.DataType}{(m.Dimension > 0 ? $"[{m.Dimension.ToString(CultureInfo.InvariantCulture)}]" : string.Empty)}"
            + (string.IsNullOrWhiteSpace(m.Description) ? string.Empty : $" // {m.Description}")));

    private static IEnumerable<(int Line, string Name, string Type, string? Dims, string? Description)> Lines(string? text, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string raw = lines[i].Trim();
            if (raw.Length == 0 || raw.StartsWith("//", StringComparison.Ordinal) || raw.StartsWith('#'))
            {
                continue;
            }

            string? description = null;
            string body = raw;

            int comment = raw.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0)
            {
                description = raw[(comment + 2)..].Trim();
                body = raw[..comment].Trim();
            }

            string[] parts;
            if (body.Contains('\t', StringComparison.Ordinal))
            {
                parts = body.Split('\t', 3, StringSplitOptions.TrimEntries);
            }
            else if (TopLevelCommas(body) is { Count: > 0 } commas)
            {
                parts = SplitAt(body, commas, 3);
            }
            else
            {
                body = body.Replace(":", " ", StringComparison.Ordinal);
                parts = body.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }

            if (parts.Length < 2)
            {
                problems.Add($"Line {i + 1}: expected a name and a type, found '{raw}'.");
                continue;
            }

            if (parts.Length == 3 && description is null && parts[2].Length > 0)
            {
                description = parts[2];
            }

            // A spreadsheet header row - "Name, Type, Description" - is not a declaration.
            if (i == 0 && string.Equals(parts[0], "Name", StringComparison.OrdinalIgnoreCase) && string.Equals(parts[1], "Type", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!LogixTypes.TryParseTypeSpec(parts[1], out string type, out string? dims))
            {
                problems.Add($"Line {i + 1}: '{parts[1]}' is not a type such as DINT, REAL[10] or a UDT name.");
                continue;
            }

            yield return (i + 1, parts[0], type, dims, string.IsNullOrWhiteSpace(description) ? null : description);
        }
    }

    /// <summary>Commas outside brackets - REAL[4,8] keeps its comma.</summary>
    private static List<int> TopLevelCommas(string s)
    {
        var at = new List<int>();
        int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            switch (s[i])
            {
                case '[':
                    depth++;
                    break;
                case ']':
                    depth--;
                    break;
                case ',' when depth == 0:
                    at.Add(i);
                    break;
            }
        }

        return at;
    }

    private static string[] SplitAt(string s, List<int> commas, int max)
    {
        var parts = new List<string>();
        int start = 0;
        foreach (int c in commas.Take(max - 1))
        {
            parts.Add(s[start..c].Trim());
            start = c + 1;
        }

        parts.Add(s[start..].Trim());
        return [.. parts];
    }
}
