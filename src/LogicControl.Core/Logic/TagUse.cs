namespace LogicControl.Core.Logic;

/// <summary>
/// One place a tag is used: which routine, which rung (or ST line), which instruction, and
/// whether it is read or written there.
/// </summary>
public sealed record TagUse(
    string Routine,
    int Location,
    string Instruction,
    string Operand,
    TagAccess Access,
    bool IsStructuredText)
{
    /// <summary>"Rung 12" or "Line 40" - what a person scrolling to it would look for.</summary>
    public string LocationText => IsStructuredText ? $"Line {Location}" : $"Rung {Location}";
}

public enum TagAccess
{
    Read,
    Write,
}
