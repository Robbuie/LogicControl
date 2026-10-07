namespace LogicControl.Core.Logic;

/// <summary>
/// Finds the tags an operand refers to.
///
/// <para>An operand can be a plain tag (<c>Start</c>), a member (<c>Timer1.DN</c>), an element
/// (<c>Recipe[Idx].Speed</c> - which also reads <c>Idx</c>), a module tag
/// (<c>Local:3:I.Data.0</c>), a literal (<c>100</c>, <c>16#FF</c>, <c>1.5e3</c>) or an expression
/// (<c>(A + B) * ABS(C)</c>). What comes back is each tag's <i>base</i> name - the part before the
/// first dot or bracket - because that is what a tag is declared as.</para>
/// </summary>
public static class TagReference
{
    /// <summary>Expression functions and operators that look like identifiers but are not tags.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "ABS", "ACS", "ASN", "ATN", "COS", "DEG", "FRD", "LN", "LOG", "RAD", "SIN", "SQR", "SQRT",
        "TAN", "TOD", "TRN", "AND", "OR", "XOR", "NOT", "MOD",

        // Structured text keywords, so the same scanner serves ST lines.
        "IF", "THEN", "ELSIF", "ELSE", "END_IF", "CASE", "OF", "END_CASE", "FOR", "TO", "BY", "DO",
        "END_FOR", "WHILE", "END_WHILE", "REPEAT", "UNTIL", "END_REPEAT", "EXIT", "RETURN",
        "TRUE", "FALSE",
    };

    /// <summary>
    /// The base names of every tag the text mentions, in order of appearance. Literals, function
    /// names, member names after a dot and the digits of radix numbers are left out.
    /// </summary>
    public static IReadOnlyList<string> BaseNames(string? text)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(text) || text.Trim() == "?")
        {
            return names;
        }

        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];

            if (c == '\'')
            {
                // A string literal - skip to its close.
                int end = text.IndexOf('\'', i + 1);
                i = end < 0 ? text.Length : end + 1;
                continue;
            }

            if (!RungParser.IsIdentStart(c))
            {
                i++;
                continue;
            }

            char before = i > 0 ? text[i - 1] : ' ';
            int start = i;

            // Module tags carry colons: Local:3:I, Rack1:O. They are one name.
            while (i < text.Length && (RungParser.IsIdentPart(text[i]) || text[i] == ':'))
            {
                i++;
            }

            string word = text[start..i].TrimEnd(':');

            // Member after a dot, the letters of 16#FF, the exponent of 1.5e3: not tags.
            if (before == '.' || before == '#' || char.IsAsciiDigit(before))
            {
                continue;
            }

            // Assignment in ST is ':=' - a word ending in ':' that is followed by '=' lost that colon above.
            int next = i;
            while (next < text.Length && char.IsWhiteSpace(text[next]))
            {
                next++;
            }

            bool isCall = next < text.Length && text[next] == '(';
            if (isCall || Reserved.Contains(word) || word.Length == 0)
            {
                continue;
            }

            names.Add(word);
        }

        return names;
    }

    /// <summary>
    /// For a module tag, the prefix that identifies the module; otherwise null.
    ///
    /// <para>Remote modules are named: <c>VFD_101:I</c> is module VFD_101. Modules in the local
    /// chassis are not - <c>Local:3:I</c> is whatever sits in slot 3 - and modules in a remote rack
    /// that are not rack-optimized are <c>Rack1:3:I</c>, slot 3 under adapter Rack1. So the prefix
    /// is the name, plus the slot when the second segment is a number: "VFD_101", "Local:3",
    /// "Rack1:3". <c>Analysis.ModuleTags</c> maps a module to the same prefix.</para>
    /// </summary>
    public static string? ModuleOf(string baseName)
    {
        int colon = baseName.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            return null;
        }

        string name = baseName[..colon];
        int next = baseName.IndexOf(':', colon + 1);
        string second = next < 0 ? baseName[(colon + 1)..] : baseName[(colon + 1)..next];
        return second.Length > 0 && second.All(char.IsAsciiDigit) ? $"{name}:{second}" : name;
    }
}
