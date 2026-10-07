namespace LogicControl.Core.Model;

/// <summary>
/// One tag, at controller scope or inside a program. AOI parameters and local tags use the same
/// record with <see cref="Usage"/> set.
/// </summary>
public sealed record TagInfo
{
    public required string Name { get; init; }

    /// <summary>Null for controller scope, otherwise the program (or AOI) that owns it.</summary>
    public string? Scope { get; init; }

    public string? DataType { get; init; }

    /// <summary>Array dimensions exactly as written, e.g. "10" or "4 8". Null for a scalar.</summary>
    public string? Dimensions { get; init; }

    public TagKind Kind { get; init; } = TagKind.Base;

    /// <summary>For an alias: what it points at, e.g. Local:1:I.Data.3.</summary>
    public string? AliasFor { get; init; }

    public string? Description { get; init; }

    /// <summary>Read/Write, Read Only or None.</summary>
    public string? ExternalAccess { get; init; }

    public bool Constant { get; init; }

    /// <summary>AOI parameters: Input, Output or InOut. Local tags and ordinary tags: null.</summary>
    public string? Usage { get; init; }

    /// <summary>AOI parameters: passed as an operand in a ladder call. InOut is always required.</summary>
    public bool Required { get; init; }

    public ConsumeInfo? Consume { get; init; }

    /// <summary>For a produced tag: how many consumers it allows.</summary>
    public int? ProduceCount { get; init; }

    /// <summary>For a MESSAGE tag: the configuration of the message it sends.</summary>
    public MessageInfo? Message { get; init; }

    /// <summary>"Line2.Speed" for a program tag, plain "Speed" at controller scope.</summary>
    public string QualifiedName => Scope is null ? Name : $"{Scope}.{Name}";

    public bool IsMessage => string.Equals(DataType, "MESSAGE", StringComparison.OrdinalIgnoreCase);
}

/// <summary>What sort of tag it is - Rockwell's TagType attribute.</summary>
public enum TagKind
{
    Base,
    Alias,
    Produced,
    Consumed,
}

/// <summary>A consumed tag: who produces it and how often it is asked for.</summary>
public sealed record ConsumeInfo(string? Producer, string? RemoteTag, double? RpiMs, bool? Unicast);

/// <summary>
/// A MESSAGE tag's configuration. The connection path is the interesting part: it is how a
/// controller reaches a device that is not in its I/O tree.
/// </summary>
public sealed record MessageInfo
{
    /// <summary>CIP Data Table Read, CIP Data Table Write, CIP Generic, PLC5 Typed Read...</summary>
    public string? MessageType { get; init; }

    /// <summary>e.g. "ENBT_Local, 2, 192.168.1.40, 1, 0".</summary>
    public string? ConnectionPath { get; init; }

    /// <summary>The tag or file address on the target.</summary>
    public string? RemoteElement { get; init; }

    /// <summary>The tag in this controller the data is read into or written from.</summary>
    public string? LocalElement { get; init; }

    public int? RequestedLength { get; init; }

    public bool Connected { get; init; }

    public bool CacheConnections { get; init; }

    /// <summary>CIP Generic: service code, class, instance, attribute - as hex strings.</summary>
    public string? ServiceCode { get; init; }

    public string? ObjectClass { get; init; }

    public string? Instance { get; init; }

    public string? Attribute { get; init; }
}
