namespace LogicControl.Core.Logic;

/// <summary>
/// Reads a rung's neutral text into the tree a ladder diagram is drawn from.
///
/// <para>The grammar is small: a rung is a series; a series is a run of instructions and branches;
/// a branch is <c>[</c> series <c>,</c> series ... <c>]</c>, and branches nest. A leg can be empty
/// (<c>[,XIC(B)]</c> is a bypass wire). Instructions and their operands are read by
/// <see cref="RungParser"/>, so the diagram and the cross-reference agree on every operand.</para>
///
/// <para>Tolerant, like the rest of the reader: an unbalanced bracket or a stray character is
/// recorded in <see cref="LadderRung.Problems"/> and the rest of the rung is still drawn, because a
/// half-drawn rung with a note under it is more use to somebody reading a broken export than an
/// error instead of a picture. The rung checker uses the same problems to refuse to export it.</para>
/// </summary>
public static class LadderParser
{
    public static LadderRung Parse(string? text)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return new LadderRung(new LadderSeries([]), problems);
        }

        int i = 0;
        LadderSeries root = ReadSeries(text, ref i, problems, depth: 0);

        // Anything after the top-level series is an unmatched ']' or ',' - note it and read on, so
        // a rung like XIC(A)]OTE(B); still shows both instructions.
        var extra = new List<LadderNode>(root.Items);
        while (i < text.Length)
        {
            char c = text[i];
            if (c == ']')
            {
                problems.Add($"Unmatched ']' at position {i + 1}.");
            }
            else if (c == ',')
            {
                problems.Add($"',' outside a branch at position {i + 1}.");
            }

            i++;
            extra.AddRange(ReadSeries(text, ref i, problems, depth: 0).Items);
        }

        return new LadderRung(new LadderSeries(extra), problems);
    }

    private static LadderSeries ReadSeries(string text, ref int i, List<string> problems, int depth)
    {
        var items = new List<LadderNode>();

        while (i < text.Length)
        {
            char c = text[i];

            if (c is ']' or ',')
            {
                // The end of this leg; the caller decides whether that is legal here.
                return new LadderSeries(items);
            }

            if (c == ';')
            {
                i++;
                if (depth > 0)
                {
                    // Leave the semicolon visible to the branch reader as an end of text.
                    i = text.Length;
                }

                continue;
            }

            if (c == '[')
            {
                int open = i;
                i++;
                var legs = new List<LadderSeries> { ReadSeries(text, ref i, problems, depth + 1) };

                while (i < text.Length && text[i] == ',')
                {
                    i++;
                    legs.Add(ReadSeries(text, ref i, problems, depth + 1));
                }

                if (i < text.Length && text[i] == ']')
                {
                    i++;
                }
                else
                {
                    problems.Add($"The branch opened at position {open + 1} is never closed.");
                }

                if (legs.Count < 2)
                {
                    problems.Add($"The branch at position {open + 1} has only one leg.");
                }

                items.Add(new LadderBranch(legs));
                continue;
            }

            if (RungParser.IsIdentStart(c))
            {
                int start = i;
                if (RungParser.TryReadInstruction(text, ref i) is { } instruction)
                {
                    items.Add(new LadderInstruction(instruction));
                }
                else
                {
                    problems.Add($"'{text[start..i]}' at position {start + 1} is not an instruction call.");
                }

                continue;
            }

            if (!char.IsWhiteSpace(c))
            {
                problems.Add($"Unexpected '{c}' at position {i + 1}.");
            }

            i++;
        }

        return new LadderSeries(items);
    }
}

/// <summary>A parsed rung: its top-level series and anything wrong with the text.</summary>
public sealed record LadderRung(LadderSeries Root, IReadOnlyList<string> Problems)
{
    public bool IsValid => Problems.Count == 0;

    /// <summary>Every instruction in drawing order, branches walked top leg first.</summary>
    public IEnumerable<Instruction> Instructions => Root.Instructions;
}

/// <summary>An item on a rung: an instruction or a branch.</summary>
public abstract record LadderNode
{
    public abstract IEnumerable<Instruction> Instructions { get; }
}

public sealed record LadderInstruction(Instruction Instruction) : LadderNode
{
    public override IEnumerable<Instruction> Instructions => [Instruction];
}

public sealed record LadderBranch(IReadOnlyList<LadderSeries> Legs) : LadderNode
{
    public override IEnumerable<Instruction> Instructions => Legs.SelectMany(l => l.Instructions);
}

/// <summary>Items in a row, left to right. An empty series is a plain wire.</summary>
public sealed record LadderSeries(IReadOnlyList<LadderNode> Items)
{
    public IEnumerable<Instruction> Instructions => Items.SelectMany(n => n.Instructions);
}
