using LogicControl.Core.Logic;
using Xunit;

namespace LogicControl.Tests;

public class TagReferenceTests
{
    [Theory]
    [InlineData("Start", new[] { "Start" })]
    [InlineData("Timer1.DN", new[] { "Timer1" })]
    [InlineData("Recipe[Idx].Speed", new[] { "Recipe", "Idx" })]
    [InlineData("Local:3:I.Data.0", new[] { "Local:3:I" })]
    [InlineData("100", new string[0])]
    [InlineData("16#FF", new string[0])]
    [InlineData("1.5e3", new string[0])]
    [InlineData("?", new string[0])]
    [InlineData("(A + B) * ABS(C) MOD 4", new[] { "A", "B", "C" })]
    [InlineData("'literal text'", new string[0])]
    [InlineData("IF Run AND NOT Fault THEN", new[] { "Run", "Fault" })]
    public void FindsBaseTagNames(string operand, string[] expected) =>
        Assert.Equal(expected, TagReference.BaseNames(operand));

    [Theory]
    [InlineData("VFD_101:I", "VFD_101")]
    [InlineData("Local:3:I", "Local:3")]
    [InlineData("Rack1:2:O", "Rack1:2")]
    [InlineData("Plain", null)]
    public void NamesTheModuleBehindAModuleTag(string baseName, string? expected) =>
        Assert.Equal(expected, TagReference.ModuleOf(baseName));

    [Theory]
    [InlineData("MOV", 0, OperandRole.Read)]
    [InlineData("MOV", 1, OperandRole.Write)]
    [InlineData("JSR", 0, OperandRole.NotATag)]
    [InlineData("GSV", 1, OperandRole.NotATag)]
    [InlineData("GSV", 3, OperandRole.Write)]
    [InlineData("XIC", 0, OperandRole.Read)]
    [InlineData("SomethingUnknown", 0, OperandRole.Read)]
    public void KnowsWhichOperandsAreWritten(string mnemonic, int index, OperandRole expected) =>
        Assert.Equal(expected, InstructionCatalog.RoleOf(mnemonic, index));

    [Fact]
    public void SubroutineParametersAreReadAndWrittenTheWayTheyMove()
    {
        // JSR(Routine, InputCount, inputs..., returns...): inputs are read, returns are written.
        Instruction jsr = RungParser.Parse("JSR(Fill,2,Level,Setpoint,Valve_Open,Fill_Done);").Single();
        Assert.Equal(
            [OperandRole.NotATag, OperandRole.NotATag, OperandRole.Read, OperandRole.Read, OperandRole.Write, OperandRole.Write],
            Enumerable.Range(0, jsr.Operands.Count).Select(i => InstructionCatalog.RoleOf(jsr, i)));

        // SBR receives into its parameters; RET hands its values back.
        Instruction sbr = RungParser.Parse("SBR(Fill_Level,Fill_Setpoint);").Single();
        Instruction ret = RungParser.Parse("RET(Fill_Open,Fill_Finished);").Single();
        Assert.All([0, 1], i => Assert.Equal(OperandRole.Write, InstructionCatalog.RoleOf(sbr, i)));
        Assert.All([0, 1], i => Assert.Equal(OperandRole.Read, InstructionCatalog.RoleOf(ret, i)));

        // A count still '?' says nothing about where the returns start: read, never a guessed write.
        Instruction unknown = RungParser.Parse("JSR(Fill,?,Level,Valve_Open);").Single();
        Assert.Equal(OperandRole.Read, InstructionCatalog.RoleOf(unknown, 3));
    }
}
