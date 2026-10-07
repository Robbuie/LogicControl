namespace LogicControl.Core.Logic;

/// <summary>
/// Turns a parsed rung into positioned shapes: rails, wires, contacts, coils and boxes, in device
/// independent pixels. The view only strokes what this returns.
///
/// <para>Here rather than in the app so the geometry is testable without a window: a branch whose
/// legs do not line up, or an output that is not pushed to the right rail, is a failing test rather
/// than something noticed on a screen. The one thing this cannot know is how wide a piece of text
/// is in the font the view uses, so the caller passes <c>measure</c>; tests pass a fixed width per
/// character.</para>
///
/// <para>Drawn the way Studio 5000 draws it: conditions from the left rail, actions pushed against
/// the right rail, each contact and coil with its tag above it, each box with a title row and one
/// row per operand. Branches stack their legs downwards and are joined by vertical wires at each
/// end; a short leg is wired through to the right.</para>
/// </summary>
public static class LadderLayout
{
    public static LadderDiagram Arrange(
        LadderRung rung,
        Func<string, double> measure,
        Func<string, InstructionShape?>? shapes = null,
        LadderMetrics? metrics = null,
        double minWidth = 0)
    {
        ArgumentNullException.ThrowIfNull(rung);
        ArgumentNullException.ThrowIfNull(measure);

        var context = new Context(measure, shapes ?? InstructionSignatures.Find, metrics ?? LadderMetrics.Default);
        LadderMetrics m = context.Metrics;

        Size root = context.Measure(rung.Root);
        double content = m.RailInset * 2 + root.Width;
        double width = Math.Max(content, minWidth);
        double extra = width - content;

        double wireY = m.VerticalPad + root.Above;
        double height = wireY + root.Below + m.VerticalPad;

        var wires = new List<LadderWire>();
        var elements = new List<LadderElement>();

        // The flexible gap: where the rung stretches to meet the right rail. Before the trailing
        // run of actions, so outputs sit against the right rail and conditions against the left.
        int split = context.FirstTrailingAction(rung.Root);

        double x = m.RailInset;
        wires.Add(new LadderWire(0, wireY, x, wireY));

        IReadOnlyList<LadderNode> items = rung.Root.Items;
        for (int i = 0; i < items.Count; i++)
        {
            if (i == split && extra > 0)
            {
                wires.Add(new LadderWire(x, wireY, x + extra, wireY));
                x += extra;
            }

            x = context.Arrange(items[i], x, wireY, wires, elements);
        }

        if (items.Count == 0)
        {
            // An empty rung is still a wire from rail to rail.
            wires.Add(new LadderWire(x, wireY, x + m.EmptyLeg, wireY));
            x += m.EmptyLeg;
        }

        if (split >= items.Count && extra > 0)
        {
            wires.Add(new LadderWire(x, wireY, x + extra, wireY));
            x += extra;
        }

        wires.Add(new LadderWire(x, wireY, x + m.RailInset, wireY));
        double right = x + m.RailInset;

        return new LadderDiagram(right, height, wireY, wires, elements);
    }

    private readonly record struct Size(double Width, double Above, double Below)
    {
        public double Height => Above + Below;
    }

    private sealed class Context(Func<string, double> measure, Func<string, InstructionShape?> shapes, LadderMetrics metrics)
    {
        private readonly Dictionary<object, Size> _sizes = new(ReferenceEqualityComparer.Instance);

        public LadderMetrics Metrics { get; } = metrics;

        public Size Measure(LadderSeries series)
        {
            if (_sizes.TryGetValue(series, out Size known))
            {
                return known;
            }

            double width = 0;
            double above = Metrics.MinAbove;
            double below = Metrics.MinBelow;

            foreach (LadderNode node in series.Items)
            {
                Size s = Measure(node);
                width += s.Width;
                above = Math.Max(above, s.Above);
                below = Math.Max(below, s.Below);
            }

            if (series.Items.Count == 0)
            {
                width = Metrics.EmptyLeg;
            }

            var size = new Size(width, above, below);
            _sizes[series] = size;
            return size;
        }

        private Size Measure(LadderNode node)
        {
            if (_sizes.TryGetValue(node, out Size known))
            {
                return known;
            }

            Size size = node switch
            {
                LadderInstruction i => MeasureInstruction(i.Instruction),
                LadderBranch b => MeasureBranch(b),
                _ => default,
            };

            _sizes[node] = size;
            return size;
        }

        private Size MeasureBranch(LadderBranch branch)
        {
            double width = 0;
            double total = 0;
            for (int i = 0; i < branch.Legs.Count; i++)
            {
                Size leg = Measure(branch.Legs[i]);
                width = Math.Max(width, leg.Width);
                total += leg.Height + (i > 0 ? Metrics.LegGap : 0);
            }

            double above = branch.Legs.Count > 0 ? Measure(branch.Legs[0]).Above : Metrics.MinAbove;
            return new Size(width + Metrics.BranchLead * 2, above, Math.Max(total - above, Metrics.MinBelow));
        }

        private Size MeasureInstruction(Instruction instruction)
        {
            InstructionShape shape = ShapeOf(instruction);
            LadderMetrics m = Metrics;

            if (shape.Symbol is LadderSymbol.Contact or LadderSymbol.Coil)
            {
                string label = Label(instruction);
                double textWidth = Math.Max(measure(label), measure(instruction.Mnemonic));
                double w = Math.Max(m.SymbolWidth, textWidth) + m.ElementPad * 2;
                return new Size(w, m.LabelHeight + m.SymbolHeight / 2, m.SymbolHeight / 2 + m.LabelHeight);
            }

            double rowsWidth = measure(instruction.Mnemonic) + m.BoxPad * 2;
            for (int i = 0; i < instruction.Operands.Count; i++)
            {
                double row = measure(shape.NameOf(i)) + m.BoxColumnGap + measure(instruction.Operands[i]) + m.BoxPad * 2;
                rowsWidth = Math.Max(rowsWidth, row);
            }

            double boxWidth = Math.Max(m.MinBoxWidth, rowsWidth);
            double boxHeight = m.BoxTitleHeight + instruction.Operands.Count * m.BoxRowHeight + m.BoxPad;
            double anchor = m.BoxTopGap + m.BoxTitleHeight / 2;
            return new Size(boxWidth + m.ElementPad * 2, anchor, m.BoxTopGap + boxHeight - anchor + m.BoxBottomGap);
        }

        public double Arrange(LadderNode node, double x, double wireY, List<LadderWire> wires, List<LadderElement> elements)
        {
            Size size = Measure(node);

            if (node is LadderInstruction li)
            {
                elements.Add(Element(li.Instruction, x, wireY, size));
                return x + size.Width;
            }

            var branch = (LadderBranch)node;
            double left = x + Metrics.BranchLead;
            double right = x + size.Width - Metrics.BranchLead;

            wires.Add(new LadderWire(x, wireY, left, wireY));
            wires.Add(new LadderWire(right, wireY, x + size.Width, wireY));

            double legWire = wireY;
            double lastWire = wireY;
            for (int i = 0; i < branch.Legs.Count; i++)
            {
                LadderSeries leg = branch.Legs[i];
                Size legSize = Measure(leg);

                if (i > 0)
                {
                    Size previous = Measure(branch.Legs[i - 1]);
                    legWire += previous.Below + Metrics.LegGap + legSize.Above;
                }

                double end = ArrangeSeries(leg, left, legWire, wires, elements);
                if (end < right)
                {
                    wires.Add(new LadderWire(end, legWire, right, legWire));
                }

                lastWire = legWire;
            }

            if (lastWire > wireY)
            {
                wires.Add(new LadderWire(left, wireY, left, lastWire));
                wires.Add(new LadderWire(right, wireY, right, lastWire));
            }

            return x + size.Width;
        }

        private double ArrangeSeries(LadderSeries series, double x, double wireY, List<LadderWire> wires, List<LadderElement> elements)
        {
            if (series.Items.Count == 0)
            {
                return x;
            }

            foreach (LadderNode node in series.Items)
            {
                x = Arrange(node, x, wireY, wires, elements);
            }

            return x;
        }

        private LadderElement Element(Instruction instruction, double x, double wireY, Size size)
        {
            InstructionShape shape = ShapeOf(instruction);
            LadderMetrics m = Metrics;
            double top = wireY - size.Above;

            if (shape.Symbol is LadderSymbol.Contact or LadderSymbol.Coil)
            {
                var symbol = new LadderRect(
                    x + (size.Width - m.SymbolWidth) / 2,
                    wireY - m.SymbolHeight / 2,
                    m.SymbolWidth,
                    m.SymbolHeight);

                return new LadderElement(
                    instruction, shape, new LadderRect(x, top, size.Width, size.Height), symbol, wireY,
                    Label(instruction), Glyph(instruction.Mnemonic), []);
            }

            var box = new LadderRect(
                x + m.ElementPad,
                top + m.BoxTopGap,
                size.Width - m.ElementPad * 2,
                m.BoxTitleHeight + instruction.Operands.Count * m.BoxRowHeight + m.BoxPad);

            List<LadderBoxRow> rows = instruction.Operands
                .Select((operand, i) => new LadderBoxRow(shape.NameOf(i), operand, i))
                .ToList();

            return new LadderElement(
                instruction, shape, new LadderRect(x, top, size.Width, size.Height), box, wireY,
                instruction.Mnemonic, null, rows);
        }

        public int FirstTrailingAction(LadderSeries series)
        {
            int split = series.Items.Count;
            for (int i = series.Items.Count - 1; i >= 0; i--)
            {
                if (!IsAction(series.Items[i]))
                {
                    break;
                }

                split = i;
            }

            return split;
        }

        private bool IsAction(LadderNode node) => node switch
        {
            LadderInstruction i => !ShapeOf(i.Instruction).IsCondition,
            LadderBranch b => b.Legs.All(l => l.Items.Count > 0 && l.Items.All(IsAction)),
            _ => false,
        };

        private InstructionShape ShapeOf(Instruction instruction) =>
            shapes(instruction.Mnemonic)
            ?? new InstructionShape(instruction.Mnemonic, LadderSymbol.OutputBox, "Unknown", instruction.Mnemonic, []);

        private static string Label(Instruction instruction) =>
            instruction.Operands.Count > 0 ? instruction.Operands[0] : string.Empty;

        /// <summary>The letter inside a symbol: L and U on latch coils, a slash on XIO.</summary>
        private static string? Glyph(string mnemonic) => mnemonic.ToUpperInvariant() switch
        {
            "XIO" => "/",
            "OTL" => "L",
            "OTU" => "U",
            "ONS" => "ONS",
            "RES" => "RES",
            "LBL" => "LBL",
            "AFI" => "AFI",
            _ => null,
        };
    }
}

/// <summary>Sizes, in device independent pixels. Defaults read well at the app's 13px mono font.</summary>
public sealed record LadderMetrics
{
    public static LadderMetrics Default { get; } = new();

    /// <summary>Space between a rail and the first or last element.</summary>
    public double RailInset { get; init; } = 14;

    public double VerticalPad { get; init; } = 6;

    /// <summary>Height of the tag name above a contact or coil.</summary>
    public double LabelHeight { get; init; } = 18;

    public double SymbolWidth { get; init; } = 30;

    public double SymbolHeight { get; init; } = 22;

    /// <summary>Wire either side of every element, so neighbours never touch.</summary>
    public double ElementPad { get; init; } = 10;

    public double MinBoxWidth { get; init; } = 150;

    public double BoxTitleHeight { get; init; } = 22;

    public double BoxRowHeight { get; init; } = 18;

    public double BoxPad { get; init; } = 8;

    public double BoxColumnGap { get; init; } = 14;

    public double BoxTopGap { get; init; } = 4;

    public double BoxBottomGap { get; init; } = 4;

    /// <summary>Wire between a branch's outer connection and its vertical joins.</summary>
    public double BranchLead { get; init; } = 8;

    public double LegGap { get; init; } = 6;

    /// <summary>The length of an empty leg - a bypass wire.</summary>
    public double EmptyLeg { get; init; } = 30;

    public double MinAbove { get; init; } = 20;

    public double MinBelow { get; init; } = 14;
}

/// <summary>A drawn rung: its size, where the main wire runs, and what to stroke.</summary>
public sealed record LadderDiagram(
    double Width,
    double Height,
    double WireY,
    IReadOnlyList<LadderWire> Wires,
    IReadOnlyList<LadderElement> Elements)
{
    /// <summary>The element under a point, for clicks and tooltips.</summary>
    public LadderElement? HitTest(double x, double y) =>
        Elements.FirstOrDefault(e => e.Bounds.Contains(x, y));
}

public readonly record struct LadderRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double CenterY => Y + Height / 2;

    public bool Contains(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;
}

/// <summary>A straight wire, horizontal or vertical.</summary>
public readonly record struct LadderWire(double X1, double Y1, double X2, double Y2);

/// <summary>
/// One instruction placed on the diagram. <see cref="Bounds"/> is its whole slot including the
/// wire either side; <see cref="Symbol"/> is the contact, coil or box itself. Contacts and coils
/// carry their tag in <see cref="Label"/>; boxes carry their mnemonic there and one
/// <see cref="Rows"/> entry per operand.
/// </summary>
public sealed record LadderElement(
    Instruction Instruction,
    InstructionShape Shape,
    LadderRect Bounds,
    LadderRect Symbol,
    double WireY,
    string Label,
    string? Glyph,
    IReadOnlyList<LadderBoxRow> Rows)
{
    public bool IsBox => Shape.Symbol is LadderSymbol.InputBox or LadderSymbol.OutputBox;
}

/// <summary>A box row: the operand's name, what is passed, and its index in the call.</summary>
public sealed record LadderBoxRow(string Name, string Value, int Index);
