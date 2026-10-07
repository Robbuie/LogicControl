namespace LogicControl.Core.Model;

/// <summary>
/// One I/O connection the controller opens to a module.
///
/// <para>L5X stores the RPI in microseconds; <see cref="RpiMs"/> is what Studio 5000 shows.</para>
/// </summary>
public sealed record ConnectionInfo
{
    public string? Name { get; init; }

    /// <summary>Requested packet interval in microseconds, as the file stores it.</summary>
    public int? RpiMicroseconds { get; init; }

    /// <summary>Input, Output, InputOutput, ListenOnly...</summary>
    public string? Type { get; init; }

    public int? InputInstance { get; init; }

    public int? OutputInstance { get; init; }

    public int? InputSize { get; init; }

    public int? OutputSize { get; init; }

    /// <summary>Null when the file did not say - older revisions only multicast.</summary>
    public bool? Unicast { get; init; }

    public double? RpiMs => RpiMicroseconds / 1000.0;
}
