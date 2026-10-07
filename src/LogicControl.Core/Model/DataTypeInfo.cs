namespace LogicControl.Core.Model;

/// <summary>A user-defined type (UDT) or a string type.</summary>
public sealed record DataTypeInfo
{
    public required string Name { get; init; }

    /// <summary>NoFamily for a UDT, StringFamily for a string type.</summary>
    public string? Family { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<DataTypeMember> Members { get; init; } = [];
}

/// <summary>
/// A UDT member. Rockwell packs BOOL members into hidden SINT hosts named ZZZZZZZZZZ...; those are
/// dropped by the reader because nobody wrote them and nobody can address them.
/// </summary>
public sealed record DataTypeMember(string Name, string? DataType, int Dimension, string? Description);
