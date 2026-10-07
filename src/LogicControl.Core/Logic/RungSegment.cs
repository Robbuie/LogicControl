namespace LogicControl.Core.Logic;

/// <summary>A run of rung text with what it is, so a view can colour it.</summary>
public sealed record RungSegment(string Text, RungSegmentKind Kind);

public enum RungSegmentKind
{
    /// <summary>An instruction name: XIC, MOV, MSG, or an AOI's name.</summary>
    Mnemonic,

    /// <summary>The parentheses and commas of an instruction call.</summary>
    Punctuation,

    /// <summary>An operand: a tag reference, literal or expression.</summary>
    Operand,

    /// <summary>The branch characters [ , ] and the closing semicolon.</summary>
    Branch,
}
