namespace LogicControl.Core.Authoring;

// The things LogicControl can write. Mutable on purpose: these are what the Develop editors bind
// to and what a .lcdev file stores, and an editor that had to rebuild a record on every keystroke
// would be all plumbing. Nothing here knows about XML - L5xWriter turns them into L5X, and
// DraftChecker says what is wrong with them first.

/// <summary>A user-defined data type to create or replace.</summary>
public sealed class UdtDraft
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public List<MemberDraft> Members { get; set; } = [];
}

/// <summary>
/// A UDT member. <see cref="Dimension"/> is 0 for a scalar; a BOOL array must be a multiple of 32,
/// which is Logix's rule, not this tool's.
/// </summary>
public sealed class MemberDraft
{
    public string Name { get; set; } = string.Empty;

    public string DataType { get; set; } = "DINT";

    public int Dimension { get; set; }

    public string? Description { get; set; }

    /// <summary>Read/Write, Read Only or None.</summary>
    public string ExternalAccess { get; set; } = "Read/Write";

    public MemberDraft()
    {
    }

    public MemberDraft(string name, string dataType, string? description = null, int dimension = 0)
    {
        Name = name;
        DataType = dataType;
        Description = description;
        Dimension = dimension;
    }
}

/// <summary>A tag to create: controller-scoped when <see cref="Program"/> is null.</summary>
public sealed class TagDraft
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Null for controller scope, otherwise the program it belongs to.</summary>
    public string? Program { get; set; }

    public string DataType { get; set; } = "DINT";

    /// <summary>Array dimensions as Logix writes them: "10", or "4 8" for two dimensions. Null for a scalar.</summary>
    public string? Dimensions { get; set; }

    public string? Description { get; set; }

    /// <summary>For an alias tag: what it points at. DataType is then ignored - an alias takes its target's.</summary>
    public string? AliasFor { get; set; }

    public bool Constant { get; set; }

    public string ExternalAccess { get; set; } = "Read/Write";

    public TagDraft()
    {
    }

    public TagDraft(string name, string dataType, string? description = null, string? program = null)
    {
        Name = name;
        DataType = dataType;
        Description = description;
        Program = program;
    }

    public string QualifiedName => Program is null ? Name : $"{Program}.{Name}";
}

/// <summary>One rung: its neutral text and an optional comment.</summary>
public sealed class RungDraft
{
    public string Text { get; set; } = string.Empty;

    public string? Comment { get; set; }

    public RungDraft()
    {
    }

    public RungDraft(string text, string? comment = null)
    {
        Text = text;
        Comment = comment;
    }
}

/// <summary>A ladder routine in a program.</summary>
public sealed class RoutineDraft
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The program it goes in. Must exist in the project or as a <see cref="ProgramDraft"/>.</summary>
    public string Program { get; set; } = "MainProgram";

    public string? Description { get; set; }

    public List<RungDraft> Rungs { get; set; } = [];

    public string QualifiedName => $"{Program}/{Name}";
}

/// <summary>
/// A program to create, with its own tags and routines. Routine and tag drafts that name this
/// program are written inside it.
/// </summary>
public sealed class ProgramDraft
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? MainRoutineName { get; set; }

    /// <summary>The task to schedule it in, when writing into a whole project. Null leaves it unscheduled.</summary>
    public string? Task { get; set; }
}

/// <summary>An Add-On Instruction definition with ladder logic.</summary>
public sealed class AoiDraft
{
    public string Name { get; set; } = string.Empty;

    public string Revision { get; set; } = "1.0";

    public string? Description { get; set; }

    /// <summary>The parameters after the system EnableIn and EnableOut, which are always written.</summary>
    public List<AoiParameterDraft> Parameters { get; set; } = [];

    public List<AoiLocalTagDraft> LocalTags { get; set; } = [];

    /// <summary>The Logic routine.</summary>
    public List<RungDraft> Logic { get; set; } = [];
}

public sealed class AoiParameterDraft
{
    public string Name { get; set; } = string.Empty;

    public string DataType { get; set; } = "BOOL";

    /// <summary>Input, Output or InOut.</summary>
    public string Usage { get; set; } = "Input";

    /// <summary>Passed as an operand in the ladder call. InOut is always required.</summary>
    public bool Required { get; set; }

    /// <summary>Shown on the instruction box. Required implies visible.</summary>
    public bool Visible { get; set; } = true;

    public string? Description { get; set; }

    public AoiParameterDraft()
    {
    }

    public AoiParameterDraft(string name, string dataType, string usage, bool required, string? description = null)
    {
        Name = name;
        DataType = dataType;
        Usage = usage;
        Required = required;
        Description = description;
    }

    public bool IsRequired => Required || string.Equals(Usage, "InOut", StringComparison.OrdinalIgnoreCase);
}

public sealed class AoiLocalTagDraft
{
    public string Name { get; set; } = string.Empty;

    public string DataType { get; set; } = "DINT";

    public int Dimension { get; set; }

    public string? Description { get; set; }

    public AoiLocalTagDraft()
    {
    }

    public AoiLocalTagDraft(string name, string dataType, string? description = null)
    {
        Name = name;
        DataType = dataType;
        Description = description;
    }
}

/// <summary>
/// A Generic Ethernet module (ETHERNET-MODULE) to add under an Ethernet bridge in the I/O tree:
/// a device the controller owns a connection to by assembly instance - a scale, a valve bank, a
/// vision sensor. Sizes are in elements of <see cref="Format"/>, the way Studio 5000's dialog asks.
/// </summary>
public sealed class ModuleDraft
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>The Ethernet bridge it hangs off: Local for the controller's own port, or an EN2T etc.</summary>
    public string ParentModule { get; set; } = "Local";

    /// <summary>The parent's Ethernet port - 2 on a 1756-EN2T and on the L8x's embedded port.</summary>
    public int ParentPortId { get; set; } = 2;

    public string IpAddress { get; set; } = string.Empty;

    /// <summary>Comm format: one of <see cref="ModuleFormats.All"/>.</summary>
    public string Format { get; set; } = ModuleFormats.Dint;

    public int InputInstance { get; set; } = 100;

    public int InputSize { get; set; } = 1;

    public int OutputInstance { get; set; } = 150;

    public int OutputSize { get; set; } = 1;

    public int ConfigInstance { get; set; } = 1;

    /// <summary>Configuration size in bytes. 0 for most devices.</summary>
    public int ConfigSize { get; set; }

    public double RpiMs { get; set; } = 20;

    public bool Unicast { get; set; } = true;
}

/// <summary>
/// The Generic Ethernet comm formats LogicControl writes, and how each is stored in L5X.
///
/// <para><b>Unverified against Studio 5000.</b> The CommMethod numbers and the byte sizes below are
/// LogicControl's reading of exports, not Rockwell documentation; the first real import of a
/// merged project (PLAN.md step A) confirms them. A wrong value shows up as Studio 5000 refusing
/// the module or showing a different format - fix the table and its test.</para>
/// </summary>
public static class ModuleFormats
{
    public const string Dint = "Data - DINT";
    public const string Int = "Data - INT";
    public const string Sint = "Data - SINT";
    public const string Real = "Data - REAL";

    public static IReadOnlyList<string> All { get; } = [Dint, Int, Sint, Real];

    private static readonly Dictionary<string, (int CommMethod, int Bytes)> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        [Dint] = (0x40000001, 4),
        [Int] = (0x40000002, 2),
        [Sint] = (0x40000003, 1),
        [Real] = (0x40000004, 4),
    };

    public static bool IsKnown(string? format) => format is not null && Table.ContainsKey(format);

    public static int CommMethod(string format) => Table.TryGetValue(format, out var f) ? f.CommMethod : Table[Dint].CommMethod;

    public static int ElementBytes(string format) => Table.TryGetValue(format, out var f) ? f.Bytes : 4;

    /// <summary>The format a CommMethod number means; Data - DINT when it is not one of these.</summary>
    public static string FromCommMethod(int? commMethod) =>
        Table.FirstOrDefault(p => p.Value.CommMethod == commMethod).Key ?? Dint;

    public static int Bytes(int elements, string format) => elements * ElementBytes(format);

    public static int Elements(int bytes, string format) => bytes / Math.Max(1, ElementBytes(format));
}
