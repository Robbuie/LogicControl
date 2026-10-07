namespace LogicControl.Core.Model;

/// <summary>A program: its own tags and routines, and which routine runs first.</summary>
public sealed record ProgramInfo
{
    public required string Name { get; init; }

    public string? MainRoutineName { get; init; }

    public string? FaultRoutineName { get; init; }

    public bool Disabled { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<TagInfo> Tags { get; init; } = [];

    public IReadOnlyList<RoutineInfo> Routines { get; init; } = [];
}

/// <summary>A task and the programs it schedules.</summary>
public sealed record TaskInfo
{
    public required string Name { get; init; }

    /// <summary>CONTINUOUS, PERIODIC or EVENT.</summary>
    public string? Type { get; init; }

    /// <summary>Periodic tasks: the period in milliseconds.</summary>
    public double? RateMs { get; init; }

    public int? Priority { get; init; }

    public int? WatchdogMs { get; init; }

    public bool InhibitTask { get; init; }

    public IReadOnlyList<string> ScheduledPrograms { get; init; } = [];
}
