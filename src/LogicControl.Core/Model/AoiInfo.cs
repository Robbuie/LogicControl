namespace LogicControl.Core.Model;

/// <summary>An Add-On Instruction definition: its interface, its local tags and its logic.</summary>
public sealed record AoiInfo
{
    public required string Name { get; init; }

    public string? Revision { get; init; }

    public string? Vendor { get; init; }

    public string? Description { get; init; }

    /// <summary>Source-protected: only the name survives in the export.</summary>
    public bool IsProtected { get; init; }

    /// <summary>The parameters in order - the order an instruction call passes them in.</summary>
    public IReadOnlyList<TagInfo> Parameters { get; init; } = [];

    public IReadOnlyList<TagInfo> LocalTags { get; init; } = [];

    public IReadOnlyList<RoutineInfo> Routines { get; init; } = [];

    /// <summary>
    /// The parameters that appear as operands in a ladder call, after the instance tag: the
    /// Required ones, in definition order. EnableIn and EnableOut are never passed.
    /// </summary>
    public IReadOnlyList<TagInfo> CallParameters { get; init; } = [];
}
