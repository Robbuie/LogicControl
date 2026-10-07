namespace LogicControl.Core.Logic;

/// <summary>
/// One instruction on a rung, as written: <c>MOV(Speed,Drive.Ref)</c> is mnemonic MOV with
/// operands "Speed" and "Drive.Ref". <see cref="Position"/> is the character offset in the rung
/// text, so a view can highlight it.
/// </summary>
public sealed record Instruction(string Mnemonic, IReadOnlyList<string> Operands, int Position);
