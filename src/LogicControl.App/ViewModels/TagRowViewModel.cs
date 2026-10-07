using LogicControl.Core.Logic;
using LogicControl.Core.Model;

namespace LogicControl.App.ViewModels;

/// <summary>One tag with how often logic reads and writes it.</summary>
public sealed class TagRowViewModel
{
    public TagRowViewModel(TagInfo tag, IReadOnlyList<TagUse> uses)
    {
        ArgumentNullException.ThrowIfNull(tag);
        ArgumentNullException.ThrowIfNull(uses);

        Tag = tag;
        Uses = uses.Select(u => new TagUseRowViewModel(u)).ToList();
        Reads = uses.Count(u => u.Access == TagAccess.Read);
        Writes = uses.Count(u => u.Access == TagAccess.Write);
    }

    public TagInfo Tag { get; }

    public string Name => Tag.Name;

    public string Scope => Tag.Scope ?? "Controller";

    public string DataType => Tag.DataType ?? (Tag.Kind == TagKind.Alias ? "(alias)" : string.Empty);

    public string Kind => Tag.Kind switch
    {
        TagKind.Alias => $"Alias → {Tag.AliasFor}",
        TagKind.Produced => "Produced",
        TagKind.Consumed => $"Consumed ← {Tag.Consume?.Producer}",
        _ => Tag.Dimensions is null ? string.Empty : $"[{Tag.Dimensions}]",
    };

    public string Description => Tag.Description ?? string.Empty;

    public int Reads { get; }

    public int Writes { get; }

    /// <summary>
    /// "Unused", "Read only", "Write only" or empty - the column people sort by. Neutral words on
    /// purpose: an input alias or a consumed tag is read-only by nature, and an HMI writes plenty of
    /// tags the logic only reads, so neither is a fault and the column should not sound like one.
    /// </summary>
    public string Usage => (Reads, Writes) switch
    {
        (0, 0) => "Unused",
        (_, 0) => "Read only",
        (0, _) => "Write only",
        _ => string.Empty,
    };

    public IReadOnlyList<TagUseRowViewModel> Uses { get; }

    public string SearchText => $"{Name} {Scope} {DataType} {Kind} {Description} {Usage}";
}

/// <summary>One place a tag is used, for the cross-reference pane.</summary>
public sealed class TagUseRowViewModel(TagUse use)
{
    public TagUse Use { get; } = use ?? throw new ArgumentNullException(nameof(use));

    public string Routine => Use.Routine;

    public string Location => Use.Routine == "(alias)" ? string.Empty : Use.LocationText;

    public string Instruction => Use.Instruction;

    public string Operand => Use.Operand;

    public string Access => Use.Access == TagAccess.Write ? "Write" : "Read";
}
