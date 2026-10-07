namespace LogicControl.Core.Model;

/// <summary>
/// A routine. Ladder and structured text are kept as text, which is how L5X stores them; function
/// block and SFC are graphical XML and are only counted for now - see PLAN.md.
/// </summary>
public sealed record RoutineInfo
{
    public required string Name { get; init; }

    /// <summary>The program or AOI that owns it.</summary>
    public required string Owner { get; init; }

    /// <summary>True when <see cref="Owner"/> is an Add-On Instruction rather than a program.</summary>
    public bool OwnerIsAoi { get; init; }

    public RoutineLanguage Language { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<RungInfo> Rungs { get; init; } = [];

    /// <summary>Structured text, one entry per line.</summary>
    public IReadOnlyList<string> StructuredText { get; init; } = [];

    /// <summary>Function block and SFC: how many sheets or steps the file had.</summary>
    public int GraphicalElementCount { get; init; }

    /// <summary>Source-protected: the file carries it encrypted, so there is no logic to read.</summary>
    public bool IsProtected { get; init; }

    public string QualifiedName => $"{Owner}/{Name}";
}

public enum RoutineLanguage
{
    Unknown,
    Ladder,
    StructuredText,
    FunctionBlock,
    Sfc,
}

/// <summary>One rung: its number, its comment, and its neutral text, e.g. XIC(Start)OTE(Motor);</summary>
public sealed record RungInfo(int Number, string Text, string? Comment, string? Type);
