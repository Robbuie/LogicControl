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
}
