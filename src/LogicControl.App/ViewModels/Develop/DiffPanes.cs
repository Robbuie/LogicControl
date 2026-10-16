using LogicControl.Core.Authoring.History;
using LogicControl.Core.Logic;

namespace LogicControl.App.ViewModels.Develop;

/// <summary>
/// What the two sides of a comparison are, in words: a heading for each pane ("Open project -
/// Line3.L5X") and a short name for the labels of a stacked view ("open project"). Before is the
/// older side - the left pane - and after the newer one, on the right.
/// </summary>
public sealed record DiffSides(string Before, string After, string BeforeShort, string AfterShort)
{
    /// <summary>For a view that does not know what it compares.</summary>
    public static DiffSides Default { get; } = new("Before", "After", "before", "after");
}

/// <summary>What a row of a two-pane comparison holds.</summary>
public enum DiffRowKind
{
    /// <summary>A sub-heading across both panes: "Rungs", "Members".</summary>
    Heading,

    /// <summary>A line across both panes that belongs to neither side: "... 12 unchanged rungs".</summary>
    Note,

    /// <summary>The older version on the left, the newer on the right; either may be empty.</summary>
    Pair,
}

/// <summary>
/// One row of a two-pane comparison. A changed rung is one row - the old rung on the left, the new
/// one on the right - so the eye reads across, not down.
/// </summary>
public sealed class DiffRowViewModel
{
    private DiffRowViewModel(DiffRowKind kind, string text, DiffCellViewModel? left, DiffCellViewModel? right)
    {
        Kind = kind;
        Text = text;
        Left = left;
        Right = right;
    }

    public DiffRowKind Kind { get; }

    /// <summary>The heading or note; empty for a pair.</summary>
    public string Text { get; }

    public DiffCellViewModel? Left { get; }

    public DiffCellViewModel? Right { get; }

    public bool IsHeading => Kind == DiffRowKind.Heading;

    public bool IsNote => Kind == DiffRowKind.Note;

    public bool IsPair => Kind == DiffRowKind.Pair;

    internal static DiffRowViewModel Heading(string text) => new(DiffRowKind.Heading, text, null, null);

    internal static DiffRowViewModel Note(string text) => new(DiffRowKind.Note, text, null, null);

    internal static DiffRowViewModel Pair(DiffCellViewModel left, DiffCellViewModel right) => new(DiffRowKind.Pair, string.Empty, left, right);
}

/// <summary>
/// One side of a row: a rung (drawn as ladder or printed as text, with its label and comment), a
/// line of text, or nothing - the side a rung or field is missing from.
/// </summary>
public sealed class DiffCellViewModel
{
    private DiffCellViewModel(DiffLineKind kind, string text, string? rung, string? label, string? comment, bool draw, Func<string, InstructionShape?>? shapes)
    {
        Kind = kind;
        Text = text;
        Rung = rung;
        Label = label ?? string.Empty;
        Comment = comment ?? string.Empty;
        IsDrawn = rung is not null && draw;
        Shapes = shapes;
    }

    /// <summary>Added, Removed, Changed or Context - the colour; Context for an empty side.</summary>
    public DiffLineKind Kind { get; }

    /// <summary>The line's text without its +/-/~ prefix; empty for a rung or an empty side.</summary>
    public string Text { get; }

    /// <summary>The rung's neutral text when this side is a rung.</summary>
    public string? Rung { get; }

    public bool IsRung => Rung is not null;

    /// <summary>The rung drawn as ladder.</summary>
    public bool IsDrawn { get; }

    /// <summary>The rung listed as neutral text.</summary>
    public bool IsRungText => IsRung && !IsDrawn;

    /// <summary>A line of text that is not a rung.</summary>
    public bool IsText => !IsRung && !IsEmpty;

    /// <summary>Nothing on this side: the rung or field is only in the other version.</summary>
    public bool IsEmpty => !IsRung && Text.Length == 0;

    /// <summary>"Rung 3", in this side's own numbering.</summary>
    public string Label { get; }

    public bool HasLabel => Label.Length > 0;

    public string Comment { get; }

    public bool HasComment => !string.IsNullOrWhiteSpace(Comment);

    /// <summary>The change bar's kind, as the Logic tab uses it: Added, Removed, Changed, or empty.</summary>
    public string Change => Kind switch
    {
        DiffLineKind.Added => "Added",
        DiffLineKind.Removed => "Removed",
        DiffLineKind.Changed => "Changed",
        _ => string.Empty,
    };

    /// <summary>The Logic tab's row style fades removed rungs; a pane says it with its colour instead.</summary>
    public bool IsRemoved => false;

    public bool IsHighlighted => false;

    public Func<string, InstructionShape?>? Shapes { get; }

    internal static DiffCellViewModel Empty { get; } = new(DiffLineKind.Context, string.Empty, null, null, null, false, null);

    internal static DiffCellViewModel OfText(DiffLineKind kind, string text) => new(kind, text, null, null, null, false, null);

    internal static DiffCellViewModel OfRung(DiffLineKind kind, string rung, string? label, string? comment, bool draw, Func<string, InstructionShape?>? shapes) =>
        new(kind, string.Empty, rung, label, comment, draw, shapes);
}

/// <summary>Turns the lines of a change, read top to bottom, into rows of two panes.</summary>
public static class DiffRows
{
    public static IReadOnlyList<DiffRowViewModel> Build(IReadOnlyList<DiffLine> lines, bool draw, Func<string, InstructionShape?>? shapes)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var rows = new List<DiffRowViewModel>(lines.Count);

        for (int i = 0; i < lines.Count; i++)
        {
            DiffLine line = lines[i];
            switch (line.Kind)
            {
                case DiffLineKind.Heading:
                    rows.Add(DiffRowViewModel.Heading(line.Text));
                    break;

                case DiffLineKind.Removed when line.IsRung:
                    if (line.IsEdit && i + 1 < lines.Count && lines[i + 1] is { Kind: DiffLineKind.Added, IsRung: true, IsEdit: true } next)
                    {
                        rows.Add(DiffRowViewModel.Pair(Rung(line, draw, shapes), Rung(next, draw, shapes)));
                        i++;
                    }
                    else
                    {
                        rows.Add(DiffRowViewModel.Pair(Rung(line, draw, shapes), DiffCellViewModel.Empty));
                    }

                    break;

                case DiffLineKind.Added when line.IsRung:
                    rows.Add(DiffRowViewModel.Pair(DiffCellViewModel.Empty, Rung(line, draw, shapes)));
                    break;

                case DiffLineKind.Context when line.IsRung:
                    rows.Add(DiffRowViewModel.Pair(
                        DiffCellViewModel.OfRung(DiffLineKind.Context, line.Rung!, line.BeforeRungLabel ?? line.RungLabel, line.BeforeRungComment ?? line.RungComment, draw, shapes),
                        Rung(line, draw, shapes)));
                    break;

                case DiffLineKind.Added:
                    rows.Add(DiffRowViewModel.Pair(DiffCellViewModel.Empty, DiffCellViewModel.OfText(DiffLineKind.Added, Unprefixed(line.Text))));
                    break;

                case DiffLineKind.Removed:
                    rows.Add(DiffRowViewModel.Pair(DiffCellViewModel.OfText(DiffLineKind.Removed, Unprefixed(line.Text)), DiffCellViewModel.Empty));
                    break;

                default:
                    // Changed or Context text: on both sides when the line says what each side has,
                    // otherwise across both panes.
                    if (line.BeforeText is { } before && line.AfterText is { } after)
                    {
                        rows.Add(DiffRowViewModel.Pair(DiffCellViewModel.OfText(line.Kind, before), DiffCellViewModel.OfText(line.Kind, after)));
                    }
                    else
                    {
                        rows.Add(DiffRowViewModel.Note(line.Text.Trim()));
                    }

                    break;
            }
        }

        return rows;
    }

    private static DiffCellViewModel Rung(DiffLine line, bool draw, Func<string, InstructionShape?>? shapes) =>
        DiffCellViewModel.OfRung(line.Kind, line.Rung!, line.RungLabel, line.RungComment, draw, shapes);

    /// <summary>"+ Description: Feed" → "Description: Feed".</summary>
    internal static string Unprefixed(string text) =>
        text.Length >= 2 && text[0] is '+' or '-' or '~' && text[1] == ' ' ? text[2..] : text.Trim();
}
