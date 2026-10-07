using System.Text;

namespace LogicControl.Core.Logic;

/// <summary>
/// Cuts a rung into coloured runs for the logic view: mnemonic, punctuation, operand, branch.
///
/// <para>Same rules as <see cref="RungParser"/> - identifiers at rung level followed by an open
/// parenthesis are instructions, and what is inside them is operands split at depth-zero commas -
/// but this one keeps every character, so joining the segments gives back the rung exactly.</para>
/// </summary>
public static class RungFormatter
{
    public static IReadOnlyList<RungSegment> Format(string? text)
    {
        var segments = new List<RungSegment>();
        if (string.IsNullOrEmpty(text))
        {
            return segments;
        }

        var branch = new StringBuilder();
        int i = 0;

        while (i < text.Length)
        {
            if (!RungParser.IsIdentStart(text[i]))
            {
                branch.Append(text[i]);
                i++;
                continue;
            }

            int start = i;
            while (i < text.Length && RungParser.IsIdentPart(text[i]))
            {
                i++;
            }

            if (i >= text.Length || text[i] != '(')
            {
                branch.Append(text, start, i - start);
                continue;
            }

            Flush(segments, branch, RungSegmentKind.Branch);
            segments.Add(new RungSegment(text[start..i], RungSegmentKind.Mnemonic));
            segments.Add(new RungSegment("(", RungSegmentKind.Punctuation));
            i++;

            var operand = new StringBuilder();
            int depth = 0;
            bool quoted = false;

            while (i < text.Length)
            {
                char c = text[i];
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
                    else if ((c is ')' or ']') && depth > 0)
                    {
                        depth--;
                    }
                    else if (c == ')' && depth == 0)
                    {
                        Flush(segments, operand, RungSegmentKind.Operand);
                        segments.Add(new RungSegment(")", RungSegmentKind.Punctuation));
                        i++;
                        break;
                    }
                    else if (c == ',' && depth == 0)
                    {
                        Flush(segments, operand, RungSegmentKind.Operand);
                        segments.Add(new RungSegment(",", RungSegmentKind.Punctuation));
                        i++;
                        continue;
                    }
                }

                operand.Append(c);
                i++;
            }

            // An unfinished rung that ran off the end mid-call.
            Flush(segments, operand, RungSegmentKind.Operand);
        }

        Flush(segments, branch, RungSegmentKind.Branch);
        return segments;
    }

    private static void Flush(List<RungSegment> segments, StringBuilder buffer, RungSegmentKind kind)
    {
        if (buffer.Length > 0)
        {
            segments.Add(new RungSegment(buffer.ToString(), kind));
            buffer.Clear();
        }
    }
}
