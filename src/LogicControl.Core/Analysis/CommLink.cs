namespace LogicControl.Core.Analysis;

/// <summary>
/// One way this controller exchanges data with something else.
///
/// <para><see cref="From"/> and <see cref="To"/> are names a person would recognise - the
/// controller, a module in the I/O tree, or an IP address when a message goes somewhere the tree
/// does not know about. <see cref="Source"/> is where it is configured: the module, the tag, or
/// the rung that fires it.</para>
/// </summary>
public sealed record CommLink
{
    public required CommKind Kind { get; init; }

    public required string From { get; init; }

    public required string To { get; init; }

    /// <summary>IP or slot path of the far end, when one is known.</summary>
    public string? Address { get; init; }

    /// <summary>Requested packet interval in ms - I/O and consumed tags only.</summary>
    public double? RpiMs { get; init; }

    /// <summary>One line on what moves: connection sizes, message type and remote tag...</summary>
    public string? Detail { get; init; }

    /// <summary>Where it is configured: module name, tag name, or Routine / Rung N.</summary>
    public string? Source { get; init; }

    public string KindText => Kind switch
    {
        CommKind.Io => "I/O connection",
        CommKind.Produced => "Produced tag",
        CommKind.Consumed => "Consumed tag",
        CommKind.Message => "Message",
        CommKind.StatusRead => "Status read (GSV)",
        _ => Kind.ToString(),
    };
}

public enum CommKind
{
    /// <summary>A scheduled connection the controller owns to a module in its I/O tree.</summary>
    Io,

    /// <summary>A tag this controller publishes for other controllers to consume.</summary>
    Produced,

    /// <summary>A tag this controller reads from another controller on a schedule.</summary>
    Consumed,

    /// <summary>An unscheduled MSG instruction - read, write or CIP Generic.</summary>
    Message,

    /// <summary>Logic that reads a module's or task's state with GSV - connection monitoring.</summary>
    StatusRead,
}
