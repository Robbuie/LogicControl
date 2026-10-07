namespace LogicControl.Core.Model;

/// <summary>The controller itself: what it is, and what firmware the project targets.</summary>
public sealed record ControllerInfo
{
    public required string Name { get; init; }

    /// <summary>The catalog number, e.g. 1756-L83E. Rockwell calls it ProcessorType.</summary>
    public string? ProcessorType { get; init; }

    public int? MajorRevision { get; init; }

    public int? MinorRevision { get; init; }

    public string? Description { get; init; }

    /// <summary>True when the file carries a SafetyInfo block - a GuardLogix project.</summary>
    public bool IsSafety { get; init; }

    /// <summary>"33.11" from the two numbers, or null when the file did not say.</summary>
    public string? Revision => MajorRevision is { } major ? $"{major}.{MinorRevision ?? 0}" : null;
}
