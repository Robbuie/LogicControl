using LogicControl.Core.Logic;
using Xunit;

namespace LogicControl.Tests;

public class LadderTests
{
    private static double Seven(string text) => text.Length * 7.0;

    [Fact]
    public void ReadsASealInCircuitAsABranchWithTwoLegs()
    {
        LadderRung rung = LadderParser.Parse("[XIC(Start),XIC(Motor)]XIO(Stop)OTE(Motor);");

        Assert.True(rung.IsValid);
        Assert.Equal(3, rung.Root.Items.Count);
        var branch = Assert.IsType<LadderBranch>(rung.Root.Items[0]);
        Assert.Equal(2, branch.Legs.Count);
        Assert.Equal(new[] { "XIC", "XIC", "XIO", "OTE" }, rung.Instructions.Select(i => i.Mnemonic));
    }

    [Fact]
    public void NestsBranchesAndKeepsEmptyLegs()
    {
        LadderRung rung = LadderParser.Parse("XIC(A)[XIC(B)[XIC(C),,XIO(D)],]OTE(E);");

        Assert.True(rung.IsValid);
        var outer = Assert.IsType<LadderBranch>(rung.Root.Items[1]);
        Assert.Equal(2, outer.Legs.Count);
        Assert.Empty(outer.Legs[1].Items);

        var inner = Assert.IsType<LadderBranch>(outer.Legs[0].Items[1]);
        Assert.Equal(3, inner.Legs.Count);
        Assert.Empty(inner.Legs[1].Items);
    }

    [Fact]
    public void AgreesWithTheFlatParserOnOperands()
    {
        const string text = "XIC(A)[CPT(Out,(A+B)*Arr[Idx,2]),MOV(Src,Dst[3])]TON(T1,1000,0);";

        Assert.Equal(
            RungParser.Parse(text).Select(i => i.Mnemonic + ":" + string.Join("|", i.Operands)),
            LadderParser.Parse(text).Instructions.Select(i => i.Mnemonic + ":" + string.Join("|", i.Operands)));
    }

    [Theory]
    [InlineData("XIC(A)[XIO(B),XIC(C)OTE(D);", "never closed")]
    [InlineData("XIC(A)]OTE(B);", "Unmatched ']'")]
    [InlineData("XIC(A)OTE B;", "not an instruction call")]
    [InlineData("XIC(A)[XIO(B)]OTE(C);", "only one leg")]
    public void RecordsProblemsAndStillReadsTheRest(string text, string problem)
    {
        LadderRung rung = LadderParser.Parse(text);

        Assert.False(rung.IsValid);
        Assert.Contains(rung.Problems, p => p.Contains(problem, StringComparison.Ordinal));
        Assert.Equal("XIC", rung.Instructions.First().Mnemonic);
    }

    [Fact]
    public void PushesOutputsAgainstTheRightRail()
    {
        LadderDiagram d = LadderLayout.Arrange(LadderParser.Parse("XIC(Start)OTE(Motor);"), Seven, minWidth: 900);

        Assert.Equal(900, d.Width);
        LadderElement contact = d.Elements[0];
        LadderElement coil = d.Elements[1];

        Assert.Equal(LadderSymbol.Contact, contact.Shape.Symbol);
        Assert.Equal(LadderSymbol.Coil, coil.Shape.Symbol);
        Assert.True(contact.Bounds.X < 40, "the contact starts at the left rail");
        Assert.Equal(900 - LadderMetrics.Default.RailInset, coil.Bounds.Right);
        Assert.Equal("Motor", coil.Label);
    }

    [Fact]
    public void LinesUpBranchLegsAndJoinsThemWithVerticalWires()
    {
        LadderDiagram d = LadderLayout.Arrange(LadderParser.Parse("[XIC(Start),XIC(Motor_Running_Feedback)]OTE(Motor);"), Seven);

        LadderElement first = d.Elements[0];
        LadderElement second = d.Elements[1];

        Assert.Equal(first.Bounds.X, second.Bounds.X);
        Assert.True(second.WireY > first.WireY);
        Assert.Equal(d.WireY, first.WireY);

        // Two verticals, at the branch's left and right, from the main wire down to the second leg.
        List<LadderWire> verticals = d.Wires.Where(w => w.X1 == w.X2).ToList();
        Assert.Equal(2, verticals.Count);
        Assert.All(verticals, v => Assert.Equal(second.WireY, v.Y2));

        // The shorter top leg is wired through to the right-hand join.
        Assert.Contains(d.Wires, w => w.Y1 == first.WireY && w.X1 == first.Bounds.Right && w.X2 == verticals.Max(v => v.X1));
    }

    [Fact]
    public void DrawsBoxesWithNamedOperandRows()
    {
        LadderDiagram d = LadderLayout.Arrange(LadderParser.Parse("TON(Delay,2000,0);"), Seven);
        LadderElement box = Assert.Single(d.Elements);

        Assert.True(box.IsBox);
        Assert.Equal("TON", box.Label);
        Assert.Equal(new[] { "Timer", "Preset", "Accum" }, box.Rows.Select(r => r.Name));
        Assert.Equal(new[] { "Delay", "2000", "0" }, box.Rows.Select(r => r.Value));
        Assert.True(box.Symbol.Y < d.WireY && box.Symbol.Bottom > d.WireY, "the wire enters the box's title row");
    }

    [Fact]
    public void DrawsAnAoiCallWithItsParameterNames()
    {
        InstructionShape aoi = InstructionShape.ForAoi("Motor_Ctl", ["Start", "Stop"]);
        LadderDiagram d = LadderLayout.Arrange(
            LadderParser.Parse("Motor_Ctl(M101,PB_Start,PB_Stop);"),
            Seven,
            name => string.Equals(name, "Motor_Ctl", StringComparison.OrdinalIgnoreCase) ? aoi : InstructionSignatures.Find(name));

        Assert.Equal(new[] { "Motor_Ctl", "Start", "Stop" }, Assert.Single(d.Elements).Rows.Select(r => r.Name));
    }

    [Fact]
    public void AnEmptyRungIsAWireFromRailToRail()
    {
        LadderDiagram d = LadderLayout.Arrange(LadderParser.Parse(";"), Seven, minWidth: 300);

        Assert.Empty(d.Elements);
        Assert.Equal(300, d.Width);
        Assert.Equal(0, d.Wires.Min(w => w.X1));
        Assert.Equal(300, d.Wires.Max(w => w.X2));
    }

    [Fact]
    public void HitTestFindsTheInstructionUnderThePointer()
    {
        LadderDiagram d = LadderLayout.Arrange(LadderParser.Parse("XIC(A)XIO(B)OTE(C);"), Seven);
        LadderElement xio = d.Elements[1];

        Assert.Equal("XIO", d.HitTest(xio.Symbol.X + 2, xio.WireY)?.Instruction.Mnemonic);
        Assert.Null(d.HitTest(-5, -5));
    }
}
