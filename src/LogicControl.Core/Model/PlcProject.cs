namespace LogicControl.Core.Model;

/// <summary>
/// Everything read out of one L5X export: the controller and what it owns.
///
/// <para>A plain snapshot of the file. Nothing here is worked out - the hardware tree, the
/// communications map, cross-references and findings are all built from this by the classes in
/// <c>Analysis</c>, so a rule can be changed without touching the reader and the reader can be
/// tested without a single rule.</para>
///
/// <para>An export does not have to be a whole controller. Studio 5000 can export one routine,
/// one program, one AOI or a rung selection, and <see cref="TargetType"/> says which this was;
/// the collections simply hold whatever the file carried.</para>
/// </summary>
public sealed record PlcProject
{
    /// <summary>Where it was read from, for the title bar and for findings that name a file.</summary>
    public required string SourcePath { get; init; }

    /// <summary>The Studio 5000 version that wrote the file, e.g. "33.00".</summary>
    public string? SoftwareRevision { get; init; }

    /// <summary>What was exported: Controller, Program, Routine, AddOnInstructionDefinition, Rung...</summary>
    public string? TargetType { get; init; }

    /// <summary>The name of what was exported.</summary>
    public string? TargetName { get; init; }

    /// <summary>The export timestamp exactly as the file states it.</summary>
    public string? ExportDate { get; init; }

    public required ControllerInfo Controller { get; init; }

    public IReadOnlyList<ModuleInfo> Modules { get; init; } = [];

    public IReadOnlyList<DataTypeInfo> DataTypes { get; init; } = [];

    public IReadOnlyList<AoiInfo> AddOnInstructions { get; init; } = [];

    /// <summary>Controller-scoped tags.</summary>
    public IReadOnlyList<TagInfo> Tags { get; init; } = [];

    public IReadOnlyList<ProgramInfo> Programs { get; init; } = [];

    public IReadOnlyList<TaskInfo> Tasks { get; init; } = [];

    /// <summary>Every tag in the project - controller scope first, then each program's.</summary>
    public IEnumerable<TagInfo> AllTags => Tags.Concat(Programs.SelectMany(p => p.Tags));

    /// <summary>Every routine, program routines first, then the AOIs' own logic.</summary>
    public IEnumerable<RoutineInfo> AllRoutines =>
        Programs.SelectMany(p => p.Routines).Concat(AddOnInstructions.SelectMany(a => a.Routines));
}
