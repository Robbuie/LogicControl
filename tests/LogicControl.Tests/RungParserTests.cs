using LogicControl.Core.Logic;
using Xunit;

namespace LogicControl.Tests;

public class RungParserTests
{
    [Fact]
    public void SplitsInstructionsAcrossBranches()
    {
        IReadOnlyList<Instruction> parsed = RungParser.Parse("XIC(A)[XIO(B),XIC(C)TON(T1,?,?)]OTE(D);");

        Assert.Equal(new[] { "XIC", "XIO", "XIC", "TON", "OTE" }, parsed.Select(i => i.Mnemonic));
        Assert.Equal(new[] { "T1", "?", "?" }, parsed[3].Operands);
    }

    [Fact]
    public void KeepsNestedParenthesesAndBracketsInsideOneOperand()
    {
        Instruction cpt = RungParser.Parse("CPT(Out,(A+B)*Arr[Idx,2]);").Single();

        Assert.Equal(new[] { "Out", "(A+B)*Arr[Idx,2]" }, cpt.Operands);
    }

    [Fact]
    public void HandlesEmptyOperandListsAndEmptyRungs()
    {
        Assert.Empty(RungParser.Parse("NOP();").Single().Operands);
        Assert.Empty(RungParser.Parse(string.Empty));
        Assert.Empty(RungParser.Parse(";"));
    }

    [Fact]
    public void SurvivesAnUnfinishedRung()
    {
        IReadOnlyList<Instruction> parsed = RungParser.Parse("XIC(A)OTE(B");

        Assert.Equal(new[] { "XIC", "OTE" }, parsed.Select(i => i.Mnemonic));
        Assert.Equal(new[] { "B" }, parsed[1].Operands);
    }

    [Fact]
    public void RecordsWhereEachInstructionStarts()
    {
        IReadOnlyList<Instruction> parsed = RungParser.Parse("XIC(A)OTE(B);");
        Assert.Equal(new[] { 0, 6 }, parsed.Select(i => i.Position));
    }
}

public class RungFormatterTests
{
    [Theory]
    [InlineData("XIC(A)[XIO(B),XIC(C)]OTE(D);")]
    [InlineData("CPT(Out,(A+B)*Arr[Idx,2]);")]
    [InlineData("NOP();")]
    [InlineData("XIC(A)OTE(B")]
    [InlineData("MSG(Msg1) ;")]
    public void SegmentsRejoinToTheOriginalRung(string rung) =>
        Assert.Equal(rung, string.Concat(RungFormatter.Format(rung).Select(s => s.Text)));

    [Fact]
    public void LabelsEachRun()
    {
        IReadOnlyList<RungSegment> s = RungFormatter.Format("XIC(A)[MOV(B,C)];");

        Assert.Equal(
            new[]
            {
                RungSegmentKind.Mnemonic, RungSegmentKind.Punctuation, RungSegmentKind.Operand, RungSegmentKind.Punctuation,
                RungSegmentKind.Branch,
                RungSegmentKind.Mnemonic, RungSegmentKind.Punctuation, RungSegmentKind.Operand, RungSegmentKind.Punctuation,
                RungSegmentKind.Operand, RungSegmentKind.Punctuation,
                RungSegmentKind.Branch,
            },
            s.Select(x => x.Kind));
    }
}
