namespace LogicControl.Core.Logic;

/// <summary>
/// How a ladder instruction looks and what it takes: the operand names a box shows, how many
/// operands it must have, and whether it is a condition (left side of the rung) or an action.
///
/// <para>The ladder view uses it to draw a TON as a box with Timer, Preset and Accum rows and an
/// XIC as a contact; the rung checker uses it to say "TON takes 3 operands, this one has 2". It is
/// a table of the common instruction set, not all of it: an instruction missing here is drawn as a
/// box with its operands numbered and is never reported as wrong for its operand count - the same
/// under-report-rather-than-invent rule as <see cref="InstructionCatalog"/>.</para>
///
/// <para>Add-On Instructions are not here; <see cref="InstructionShape.ForAoi"/> builds a shape
/// from the AOI's definition, so a call is checked against the AOI the project actually has.</para>
/// </summary>
public static class InstructionSignatures
{
    private static readonly Dictionary<string, InstructionShape> Table = Build();

    /// <summary>The shape of <paramref name="mnemonic"/>, or null when this table does not know it.</summary>
    public static InstructionShape? Find(string mnemonic) =>
        Table.TryGetValue(mnemonic, out InstructionShape? shape) ? shape : null;

    /// <summary>Every instruction this table knows, for the editor's completion list.</summary>
    public static IEnumerable<InstructionShape> All => Table.Values.OrderBy(s => s.Mnemonic, StringComparer.Ordinal);

    private static Dictionary<string, InstructionShape> Build()
    {
        var t = new Dictionary<string, InstructionShape>(StringComparer.OrdinalIgnoreCase);

        void Add(string mnemonic, LadderSymbol symbol, string group, string summary, params string[] operands) =>
            t[mnemonic] = new InstructionShape(mnemonic, symbol, group, summary, operands);

        // Bit
        Add("XIC", LadderSymbol.Contact, "Bit", "Examine if closed - true when the bit is 1", "Bit");
        Add("XIO", LadderSymbol.Contact, "Bit", "Examine if open - true when the bit is 0", "Bit");
        Add("OTE", LadderSymbol.Coil, "Bit", "Output energize - the bit follows the rung", "Bit");
        Add("OTL", LadderSymbol.Coil, "Bit", "Output latch - sets the bit when the rung is true", "Bit");
        Add("OTU", LadderSymbol.Coil, "Bit", "Output unlatch - clears the bit when the rung is true", "Bit");
        Add("ONS", LadderSymbol.Contact, "Bit", "One shot - true for one scan on a rising rung", "Storage Bit");
        Add("OSR", LadderSymbol.OutputBox, "Bit", "One shot rising - sets the output bit for one scan", "Storage Bit", "Output Bit");
        Add("OSF", LadderSymbol.OutputBox, "Bit", "One shot falling - sets the output bit for one scan", "Storage Bit", "Output Bit");

        // Timers and counters
        Add("TON", LadderSymbol.OutputBox, "Timer/Counter", "Timer on delay", "Timer", "Preset", "Accum");
        Add("TOF", LadderSymbol.OutputBox, "Timer/Counter", "Timer off delay", "Timer", "Preset", "Accum");
        Add("RTO", LadderSymbol.OutputBox, "Timer/Counter", "Retentive timer on", "Timer", "Preset", "Accum");
        Add("CTU", LadderSymbol.OutputBox, "Timer/Counter", "Count up", "Counter", "Preset", "Accum");
        Add("CTD", LadderSymbol.OutputBox, "Timer/Counter", "Count down", "Counter", "Preset", "Accum");
        Add("RES", LadderSymbol.Coil, "Timer/Counter", "Reset a timer, counter or control", "Structure");

        // Compare - conditions, drawn as boxes on the input side
        foreach (string c in new[] { "EQU", "NEQ", "GRT", "GEQ", "LES", "LEQ" })
        {
            Add(c, LadderSymbol.InputBox, "Compare", CompareSummary(c), "Source A", "Source B");
        }

        Add("LIM", LadderSymbol.InputBox, "Compare", "Limit test", "Low Limit", "Test", "High Limit");
        Add("MEQ", LadderSymbol.InputBox, "Compare", "Masked equal", "Source", "Mask", "Compare");
        Add("CMP", LadderSymbol.InputBox, "Compare", "Compare an expression", "Expression");

        // Move and logical
        Add("MOV", LadderSymbol.OutputBox, "Move/Logical", "Move", "Source", "Dest");
        Add("MVM", LadderSymbol.OutputBox, "Move/Logical", "Masked move", "Source", "Mask", "Dest");
        Add("COP", LadderSymbol.OutputBox, "Move/Logical", "Copy file", "Source", "Dest", "Length");
        Add("CPS", LadderSymbol.OutputBox, "Move/Logical", "Synchronous copy file", "Source", "Dest", "Length");
        Add("FLL", LadderSymbol.OutputBox, "Move/Logical", "File fill", "Source", "Dest", "Length");
        Add("CLR", LadderSymbol.OutputBox, "Move/Logical", "Clear", "Dest");
        Add("BTD", LadderSymbol.OutputBox, "Move/Logical", "Bit field distribute", "Source", "Source Bit", "Dest", "Dest Bit", "Length");
        Add("SWPB", LadderSymbol.OutputBox, "Move/Logical", "Swap byte", "Source", "Order Mode", "Dest");
        foreach (string b in new[] { "AND", "OR", "XOR", "BAND", "BOR", "BXOR" })
        {
            Add(b, LadderSymbol.OutputBox, "Move/Logical", $"Bitwise {b.TrimStart('B')}", "Source A", "Source B", "Dest");
        }

        Add("NOT", LadderSymbol.OutputBox, "Move/Logical", "Bitwise NOT", "Source", "Dest");
        Add("BNOT", LadderSymbol.OutputBox, "Move/Logical", "Bitwise NOT", "Source", "Dest");

        // Maths
        Add("ADD", LadderSymbol.OutputBox, "Compute/Math", "Add", "Source A", "Source B", "Dest");
        Add("SUB", LadderSymbol.OutputBox, "Compute/Math", "Subtract", "Source A", "Source B", "Dest");
        Add("MUL", LadderSymbol.OutputBox, "Compute/Math", "Multiply", "Source A", "Source B", "Dest");
        Add("DIV", LadderSymbol.OutputBox, "Compute/Math", "Divide", "Source A", "Source B", "Dest");
        Add("MOD", LadderSymbol.OutputBox, "Compute/Math", "Modulo", "Source A", "Source B", "Dest");
        Add("XPY", LadderSymbol.OutputBox, "Compute/Math", "X to the power of Y", "Source X", "Source Y", "Dest");
        Add("CPT", LadderSymbol.OutputBox, "Compute/Math", "Compute an expression", "Dest", "Expression");
        foreach (string u in new[] { "SQR", "SQRT", "NEG", "ABS", "SIN", "COS", "TAN", "ASN", "ACS", "ATN", "LN", "LOG", "DEG", "RAD", "TRN", "TOD", "FRD" })
        {
            Add(u, LadderSymbol.OutputBox, "Compute/Math", u, "Source", "Dest");
        }

        Add("SCP", LadderSymbol.OutputBox, "Compute/Math", "Scale with parameters", "Input", "Input Min", "Input Max", "Scaled Min", "Scaled Max", "Output");

        // Program control
        Add("JMP", LadderSymbol.OutputBox, "Program Control", "Jump to label", "Label Name");
        Add("LBL", LadderSymbol.Contact, "Program Control", "Label", "Label Name");
        Add("RET", LadderSymbol.OutputBox, "Program Control", "Return from subroutine");
        Add("SBR", LadderSymbol.InputBox, "Program Control", "Subroutine parameters");
        Add("TND", LadderSymbol.OutputBox, "Program Control", "Temporary end");
        Add("MCR", LadderSymbol.OutputBox, "Program Control", "Master control reset");
        Add("AFI", LadderSymbol.Contact, "Program Control", "Always false - disables the rung");
        Add("NOP", LadderSymbol.OutputBox, "Program Control", "No operation");
        Add("UID", LadderSymbol.OutputBox, "Program Control", "User interrupt disable");
        Add("UIE", LadderSymbol.OutputBox, "Program Control", "User interrupt enable");
        Add("EOT", LadderSymbol.OutputBox, "Program Control", "End of transition", "Data Bit");

        // JSR takes a routine name, an input count and that many parameters: shape is variable.
        t["JSR"] = new InstructionShape("JSR", LadderSymbol.OutputBox, "Program Control", "Jump to subroutine",
            ["Routine Name", "Input Par"], VariableTail: "Par");

        // Shift and file
        Add("BSL", LadderSymbol.OutputBox, "File/Shift", "Bit shift left", "Array", "Control", "Source Bit", "Length");
        Add("BSR", LadderSymbol.OutputBox, "File/Shift", "Bit shift right", "Array", "Control", "Source Bit", "Length");
        Add("FFL", LadderSymbol.OutputBox, "File/Shift", "FIFO load", "Source", "FIFO", "Control", "Length", "Position");
        Add("FFU", LadderSymbol.OutputBox, "File/Shift", "FIFO unload", "FIFO", "Dest", "Control", "Length", "Position");
        Add("LFL", LadderSymbol.OutputBox, "File/Shift", "LIFO load", "Source", "LIFO", "Control", "Length", "Position");
        Add("LFU", LadderSymbol.OutputBox, "File/Shift", "LIFO unload", "LIFO", "Dest", "Control", "Length", "Position");
        Add("SQO", LadderSymbol.OutputBox, "File/Shift", "Sequencer output", "Array", "Mask", "Dest", "Control", "Length", "Position");
        Add("SQI", LadderSymbol.InputBox, "File/Shift", "Sequencer input", "Array", "Mask", "Source", "Control", "Length", "Position");

        // Strings
        Add("CONCAT", LadderSymbol.OutputBox, "String", "String concatenate", "Source A", "Source B", "Dest");
        Add("MID", LadderSymbol.OutputBox, "String", "Middle string", "Source", "Qty", "Start", "Dest");
        Add("DELETE", LadderSymbol.OutputBox, "String", "String delete", "Source", "Qty", "Start", "Dest");
        Add("INSERT", LadderSymbol.OutputBox, "String", "Insert string", "Source A", "Source B", "Start", "Dest");
        Add("FIND", LadderSymbol.OutputBox, "String", "Find string", "Source", "Search", "Start", "Result");
        Add("DTOS", LadderSymbol.OutputBox, "String", "DINT to string", "Source", "Dest");
        Add("STOD", LadderSymbol.OutputBox, "String", "String to DINT", "Source", "Dest");
        Add("RTOS", LadderSymbol.OutputBox, "String", "REAL to string", "Source", "Dest");
        Add("STOR", LadderSymbol.OutputBox, "String", "String to REAL", "Source", "Dest");
        Add("UPPER", LadderSymbol.OutputBox, "String", "Upper case", "Source", "Dest");
        Add("LOWER", LadderSymbol.OutputBox, "String", "Lower case", "Source", "Dest");

        // Input/output and system
        Add("MSG", LadderSymbol.OutputBox, "Input/Output", "Message", "Message Control");
        Add("GSV", LadderSymbol.OutputBox, "Input/Output", "Get system value", "Class Name", "Instance Name", "Attribute Name", "Dest");
        Add("SSV", LadderSymbol.OutputBox, "Input/Output", "Set system value", "Class Name", "Instance Name", "Attribute Name", "Source");
        Add("IOT", LadderSymbol.OutputBox, "Input/Output", "Immediate output", "Output Tag");
        Add("PID", LadderSymbol.OutputBox, "Special", "PID loop",
            "PID", "Process Variable", "Tieback", "Control Variable", "PID Master Loop", "Inhold Bit", "Inhold Value");

        return t;
    }

    private static string CompareSummary(string mnemonic) => mnemonic switch
    {
        "EQU" => "Equal",
        "NEQ" => "Not equal",
        "GRT" => "Greater than",
        "GEQ" => "Greater than or equal",
        "LES" => "Less than",
        _ => "Less than or equal",
    };
}

/// <summary>How an instruction is drawn.</summary>
public enum LadderSymbol
{
    /// <summary>-| |- : a condition with one bit operand above it.</summary>
    Contact,

    /// <summary>-( )- : an output with one operand above it.</summary>
    Coil,

    /// <summary>A box on the condition side of the rung - compares, limit tests.</summary>
    InputBox,

    /// <summary>A box on the action side - timers, maths, moves, messages, AOI calls.</summary>
    OutputBox,
}

/// <summary>
/// One instruction's shape: how it is drawn, which group it belongs to, and the name of each
/// operand in order. <see cref="VariableTail"/> is for instructions with a repeating tail (JSR's
/// parameters): any number of further operands is allowed and they are named Par 1, Par 2...
/// </summary>
public sealed record InstructionShape(
    string Mnemonic,
    LadderSymbol Symbol,
    string Group,
    string Summary,
    IReadOnlyList<string> OperandNames,
    string? VariableTail = null)
{
    /// <summary>True for a condition: contacts and input boxes.</summary>
    public bool IsCondition => Symbol is LadderSymbol.Contact or LadderSymbol.InputBox;

    /// <summary>The fewest operands a call can have.</summary>
    public int MinOperands => OperandNames.Count;

    /// <summary>The most, or null when the tail repeats.</summary>
    public int? MaxOperands => VariableTail is null ? OperandNames.Count : null;

    /// <summary>The name shown beside operand <paramref name="index"/>.</summary>
    public string NameOf(int index) =>
        index < OperandNames.Count ? OperandNames[index]
        : VariableTail is not null ? $"{VariableTail} {index - OperandNames.Count + 1}"
        : $"Operand {index + 1}";

    /// <summary>
    /// The shape of a call to an Add-On Instruction: the instance tag, then the required
    /// parameters in definition order - the operands a ladder call passes.
    /// </summary>
    public static InstructionShape ForAoi(string name, IEnumerable<string> requiredParameters, string? description = null) =>
        new(name, LadderSymbol.OutputBox, "Add-On", description ?? "Add-On Instruction",
            [name, .. requiredParameters]);
}
