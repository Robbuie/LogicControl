using System.Text;

namespace LogicControl.Core.Logic;

/// <summary>
/// Splits a rung's neutral text into its instructions.
///
/// <para>L5X stores ladder as text: <c>XIC(Start)[XIO(Stop),OTE(Seal)]OTE(Motor);</c>. At rung
/// level there are only instructions and the branch characters <c>[ , ]</c>. Inside an
/// instruction's parentheses, commas separate operands - but operands can contain their own
/// parentheses and brackets (<c>CPT(Out,(A+B)*C[Idx])</c>), so the split counts depth rather than
/// splitting on every comma.</para>
///
/// <para>Branch structure is not kept. Everything cross-reference and the findings do with a rung -
/// which tags it reads and writes, which messages it fires, which routines it calls - is the same
/// on every branch. The ladder view needs the tree, and gets it from <see cref="LadderParser"/>,
/// which reads instructions through this class so the two never disagree about an operand.</para>
/// </summary>
public static class RungParser
{
    public static IReadOnlyList<Instruction> Parse(string? text)
    {
        var result = new List<Instruction>();
        if (string.IsNullOrEmpty(text))
        {
            return result;
        }

        int i = 0;
        while (i < text.Length)
        {
            if (!IsIdentStart(text[i]))
            {
                // [ , ] ; whitespace - rung structure, nothing to collect.
                i++;
                continue;
            }

            if (TryReadInstruction(text, ref i) is { } instruction)
            {
                result.Add(instruction);
            }
        }

        return result;
    }

    /// <summary>
    /// Reads the instruction starting at <paramref name="i"/>, which must be on an identifier, and
    /// moves <paramref name="i"/> past it. Returns null - with <paramref name="i"/> past the
    /// identifier - when the identifier is not followed by an open parenthesis, which only happens
    /// in a malformed rung. Shared with <see cref="LadderParser"/>, so both read operands the same way.
    /// </summary>
    internal static Instruction? TryReadInstruction(string text, ref int i)
    {
        int start = i;
        while (i < text.Length && IsIdentPart(text[i]))
        {
            i++;
        }

        string mnemonic = text[start..i];

        int afterName = i;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        if (i >= text.Length || text[i] != '(')
        {
            // Not an instruction - stray text in a malformed rung. Skip it rather than guess.
            i = afterName;
            return null;
        }

        int close = FindClose(text, i);
        string inner = text[(i + 1)..Math.Min(close, text.Length)];
        i = Math.Min(close + 1, text.Length);
        return new Instruction(mnemonic, SplitOperands(inner), start);
    }

    /// <summary>
    /// The index of the parenthesis that closes the one at <paramref name="open"/>, or the end of
    /// the text when an unfinished rung never closes it.
    /// </summary>
    private static int FindClose(string text, int open)
    {
        int depth = 0;
        bool quoted = false;
        for (int i = open; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\'')
            {
                quoted = !quoted;
            }
            else if (quoted)
            {
                continue;
            }
            else if (c is '(' or '[')
            {
                depth++;
            }
            else if (c is ')' or ']')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return text.Length;
    }

    internal static List<string> SplitOperands(string inner)
    {
        var operands = new List<string>();
        var current = new StringBuilder();
        int depth = 0;
        bool quoted = false;

        foreach (char c in inner)
        {
            if (c == '\'')
            {
                quoted = !quoted;
            }

            if (!quoted)
            {
                if (c is '(' or '[')
                {
                    depth++;
                }
                else if (c is ')' or ']')
                {
                    depth--;
                }
                else if (c == ',' && depth == 0)
                {
                    operands.Add(current.ToString().Trim());
                    current.Clear();
                    continue;
                }
            }

            current.Append(c);
        }

        string last = current.ToString().Trim();
        if (last.Length > 0 || operands.Count > 0)
        {
            operands.Add(last);
        }

        return operands;
    }

    internal static bool IsIdentStart(char c) => char.IsAsciiLetter(c) || c == '_';

    internal static bool IsIdentPart(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
}
