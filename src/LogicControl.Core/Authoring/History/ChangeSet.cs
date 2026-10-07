using System.Globalization;

namespace LogicControl.Core.Authoring.History;

/// <summary>How one draft differs between two versions.</summary>
public enum ItemChangeKind
{
    Added,
    Removed,
    Changed,
}

/// <summary>The kind of a line in a change's detail - what colour the view gives it.</summary>
public enum DiffLineKind
{
    /// <summary>A sub-heading inside an item: "Rungs", "Members".</summary>
    Heading,
    Added,
    Removed,

    /// <summary>A field whose value changed: "Description: old → new".</summary>
    Changed,

    /// <summary>An unchanged rung shown for orientation, or "12 unchanged rungs".</summary>
    Context,
}

public sealed record DiffLine(DiffLineKind Kind, string Text);

/// <summary>
/// One draft that is different: what it is, what happened to it, and the detail - rung by rung,
/// member by member - as lines a person reads top to bottom.
/// </summary>
public sealed record ItemChange(string What, string Name, ItemChangeKind Kind, IReadOnlyList<DiffLine> Lines)
{
    /// <summary>"Routine MainProgram/Motors" - the heading of this change in a list.</summary>
    public string Title => $"{What} {Name}";

    /// <summary>"3 rungs changed, 1 added" - a short count for the history list.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>For a routine or AOI: the rung numbers (in the newer version) that were added or changed.</summary>
    public IReadOnlyList<int> TouchedRungs { get; init; } = [];
}

/// <summary>Everything that differs between two versions of a development set.</summary>
public sealed record ChangeSet(IReadOnlyList<ItemChange> Items)
{
    public static ChangeSet None { get; } = new([]);

    public bool IsEmpty => Items.Count == 0;

    /// <summary>"Routine MainProgram/Motors: 2 rungs changed; Tag Speed added" - one line.</summary>
    public string Summary => Items.Count == 0
        ? "No changes."
        : string.Join("; ", Items.Take(4).Select(i => i.Summary.Length > 0 ? $"{i.Title}: {i.Summary}" : $"{i.Title} {Verb(i.Kind)}"))
          + (Items.Count > 4 ? $"; and {(Items.Count - 4).ToString(CultureInfo.InvariantCulture)} more" : string.Empty);

    internal static string Verb(ItemChangeKind kind) => kind switch
    {
        ItemChangeKind.Added => "added",
        ItemChangeKind.Removed => "removed",
        _ => "changed",
    };
}
