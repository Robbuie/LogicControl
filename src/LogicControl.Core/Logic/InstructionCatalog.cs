namespace LogicControl.Core.Logic;

/// <summary>
/// What the ladder instructions do with their operands - which ones they write, which operands
/// are not tags at all - and what kind of instruction each is.
///
/// <para>Deliberately a table rather than a model of the instruction set. It answers the
/// questions cross-reference and the findings need ("does this rung write Motor?", "which rungs
/// fire a MSG?") and nothing more. An instruction missing from it is treated as read-only, which
/// under-reports writes rather than inventing them - the safe direction for a findings list.</para>
/// </summary>
public static class InstructionCatalog
{
    /// <summary>Operand indexes each instruction writes. Everything else it reads.</summary>
    private static readonly Dictionary<string, int[]> Writes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Outputs and one-shots
        ["OTE"] = [0], ["OTL"] = [0], ["OTU"] = [0], ["ONS"] = [0],
        ["OSR"] = [0, 1], ["OSF"] = [0, 1],

        // Timers and counters write their own structure
        ["TON"] = [0], ["TOF"] = [0], ["RTO"] = [0], ["CTU"] = [0], ["CTD"] = [0], ["RES"] = [0],
        ["TONR"] = [0], ["TOFR"] = [0], ["RTOR"] = [0], ["CTUD"] = [0],

        // Moves and copies
        ["MOV"] = [1], ["MVM"] = [2], ["COP"] = [1], ["CPS"] = [1], ["FLL"] = [1], ["CLR"] = [0],
        ["BTD"] = [2], ["SWPB"] = [2], ["CPT"] = [0],

        // Maths with a destination
        ["ADD"] = [2], ["SUB"] = [2], ["MUL"] = [2], ["DIV"] = [2], ["MOD"] = [2],
        ["AND"] = [2], ["OR"] = [2], ["XOR"] = [2], ["BAND"] = [2], ["BOR"] = [2], ["BXOR"] = [2],
        ["NOT"] = [1], ["BNOT"] = [1], ["NEG"] = [1], ["ABS"] = [1], ["SQR"] = [1], ["SQRT"] = [1],
        ["SIN"] = [1], ["COS"] = [1], ["TAN"] = [1], ["ASN"] = [1], ["ACS"] = [1], ["ATN"] = [1],
        ["LN"] = [1], ["LOG"] = [1], ["XPY"] = [2], ["DEG"] = [1], ["RAD"] = [1],
        ["TRN"] = [1], ["TOD"] = [1], ["FRD"] = [1], ["SCP"] = [5],

        // Strings
        ["CONCAT"] = [2], ["MID"] = [3], ["DELETE"] = [3], ["INSERT"] = [3], ["DTOS"] = [1],
        ["STOD"] = [1], ["RTOS"] = [1], ["STOR"] = [1], ["UPPER"] = [1], ["LOWER"] = [1],

        // Shift registers, FIFO/LIFO
        ["BSL"] = [0, 1], ["BSR"] = [0, 1], ["FFL"] = [1, 2], ["FFU"] = [0, 1, 2],
        ["LFL"] = [1, 2], ["LFU"] = [0, 1, 2], ["SQO"] = [3, 4], ["SQI"] = [3],

        // System and communications
        ["GSV"] = [3], ["MSG"] = [0], ["PID"] = [0, 3],
    };

    /// <summary>Instructions whose first operand is a routine or label name, not a tag.</summary>
    private static readonly HashSet<string> FirstOperandIsName = new(StringComparer.OrdinalIgnoreCase)
    {
        "JSR", "JMP", "LBL", "SFR", "SFP", "FOR",
    };

    /// <summary>Instructions with no tag operands at all.</summary>
    private static readonly HashSet<string> NoTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "NOP", "AFI", "TND", "MCR", "UID", "UIE", "EOT",
    };

    /// <summary>GSV/SSV take an object class, an instance name and an attribute before the value.</summary>
    private static readonly HashSet<string> SystemValue = new(StringComparer.OrdinalIgnoreCase) { "GSV", "SSV" };

    /// <summary>
    /// Whether operand <paramref name="index"/> of <paramref name="mnemonic"/> is a tag reference
    /// at all, and whether it is written.
    /// </summary>
    public static OperandRole RoleOf(string mnemonic, int index)
    {
        if (NoTags.Contains(mnemonic))
        {
            return OperandRole.NotATag;
        }

        if (index == 0 && FirstOperandIsName.Contains(mnemonic))
        {
            return OperandRole.NotATag;
        }

        // GSV(Module, Rack1, EntryStatus, Dest): class and attribute are names; the instance is a
        // module or task name, which the communications map reads separately.
        if (SystemValue.Contains(mnemonic) && index <= 2)
        {
            return OperandRole.NotATag;
        }

        return Writes.TryGetValue(mnemonic, out int[]? written) && written.Contains(index)
            ? OperandRole.Write
            : OperandRole.Read;
    }

    /// <summary>True when the instruction is part of the built-in set this table knows.</summary>
    public static bool IsKnown(string mnemonic) =>
        Writes.ContainsKey(mnemonic) || FirstOperandIsName.Contains(mnemonic) || NoTags.Contains(mnemonic)
        || SystemValue.Contains(mnemonic) || ReadOnly.Contains(mnemonic);

    /// <summary>Built-in instructions that only read - listed so an AOI with the same name is not assumed.</summary>
    private static readonly HashSet<string> ReadOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        "XIC", "XIO", "EQU", "NEQ", "GRT", "GEQ", "LES", "LEQ", "LIM", "MEQ", "CMP", "IOT",
        "RET", "SBR", "SSV", "JXR", "TRG",
    };

    /// <summary>Destructive outputs: two of these on the same bit is the classic double coil.</summary>
    public static bool IsCoil(string mnemonic) => string.Equals(mnemonic, "OTE", StringComparison.OrdinalIgnoreCase);
}

/// <summary>What an instruction does with one of its operands.</summary>
public enum OperandRole
{
    NotATag,
    Read,
    Write,
}
