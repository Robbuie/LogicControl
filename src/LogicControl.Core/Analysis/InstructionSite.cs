using LogicControl.Core.Logic;
using LogicControl.Core.Model;

namespace LogicControl.Core.Analysis;

/// <summary>One instruction where it sits: routine, rung or ST line, and the instruction itself.</summary>
public sealed record InstructionSite(RoutineInfo Routine, int Location, Instruction Instruction)
{
    public string LocationText =>
        Routine.Language == RoutineLanguage.StructuredText ? $"Line {Location}" : $"Rung {Location}";
}
