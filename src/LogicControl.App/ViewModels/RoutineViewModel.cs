using System.Globalization;
using LogicControl.App.Composition;
using LogicControl.Core.Authoring;
using LogicControl.Core.Authoring.History;
using LogicControl.Core.Logic;
using LogicControl.Core.Model;

namespace LogicControl.App.ViewModels;

/// <summary>A routine opened in the logic view: one line per rung or ST line.</summary>
public sealed class RoutineViewModel
{
    /// <param name="edit">
    /// The rungs of a draft that edits this routine in place. When given, the lines are the
    /// draft's, each marked with how it differs from the project, and the project's removed rungs
    /// are shown struck through where they were.
    /// </param>
    public RoutineViewModel(RoutineInfo routine, IEnumerable<AoiInfo>? aois = null, IReadOnlyList<RungDraft>? edit = null)
    {
        ArgumentNullException.ThrowIfNull(routine);

        Routine = routine;
        Title = routine.QualifiedName;
        Shapes = ShapeLookup(aois);
        IsLadder = routine.Language == RoutineLanguage.Ladder && !routine.IsProtected;

        if (edit is not null && IsLadder)
        {
            IsEdited = true;
            Lines = EditedLines(routine, edit, out string summary);
            EditSummary = summary;
            Summary = $"Ladder · {edit.Count.ToString(CultureInfo.InvariantCulture)} rungs · edited: {summary}";
        }
        else if (routine.IsProtected)
        {
            Summary = "Source-protected - the export does not carry its logic.";
        }
        else if (routine.Language == RoutineLanguage.Ladder)
        {
            Lines = routine.Rungs.Select(r => new LogicLineViewModel(
                r.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                r.Comment,
                RungFormatter.Format(r.Text),
                r.Text)
            { Location = r.Number, ProjectLocation = r.Number }).ToList();
            Summary = $"Ladder · {routine.Rungs.Count} rungs";
        }
        else if (routine.Language == RoutineLanguage.StructuredText)
        {
            Lines = routine.StructuredText.Select((text, i) => new LogicLineViewModel(
                i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                comment: null,
                [new RungSegment(text, RungSegmentKind.Operand)])
            { Location = i, ProjectLocation = i }).ToList();
            Summary = $"Structured text · {routine.StructuredText.Count} lines";
        }
        else
        {
            Summary = $"{routine.Language} · {routine.GraphicalElementCount} sheets or steps. "
                + "Function block and SFC are graphical and not drawn yet.";
        }
    }

    public RoutineInfo Routine { get; }

    /// <summary>The lines are a draft's edit of the routine, not the project's own.</summary>
    public bool IsEdited { get; }

    /// <summary>"2 rungs changed, 1 added" - empty when the edit changes nothing yet.</summary>
    public string EditSummary { get; } = string.Empty;

    public string Title { get; }

    public string Summary { get; }

    public IReadOnlyList<LogicLineViewModel> Lines { get; } = [];

    /// <summary>
    /// Marks the project's rung numbered (or ST line at) <paramref name="location"/> - wherever an
    /// edit has moved it - and returns its index in
    /// <see cref="Lines"/> for the view to scroll to, or -1 when there is no such line. Null clears
    /// the mark - a finding about the routine as a whole.
    /// </summary>
    public int Highlight(int? location)
    {
        int index = -1;
        for (int i = 0; i < Lines.Count; i++)
        {
            bool hit = location is { } at && Lines[i].ProjectLocation == at;
            Lines[i].IsHighlighted = hit;
            if (hit && index < 0)
            {
                index = i;
            }
        }

        return index;
    }

    /// <summary>True when the routine can be drawn as ladder - an unprotected RLL routine.</summary>
    public bool IsLadder { get; }

    /// <summary>
    /// How each instruction on these rungs is drawn: the built-in table, then the project's AOIs,
    /// so an AOI call shows a box with its parameter names rather than numbered operands.
    /// </summary>
    public Func<string, InstructionShape?> Shapes { get; }

    private static List<LogicLineViewModel> EditedLines(RoutineInfo routine, IReadOnlyList<RungDraft> edit, out string summary)
    {
        List<RungDraft> original = routine.Rungs.Select(r => new RungDraft(r.Text, r.Comment)).ToList();
        IReadOnlyList<RungRow> rows = RungComparison.Compare(original, edit);
        var lines = new List<LogicLineViewModel>(rows.Count);
        int changed = 0, added = 0, removed = 0, comments = 0;

        foreach (RungRow row in rows)
        {
            if (row.Change == RungChange.Removed)
            {
                removed++;
                RungDraft was = original[row.Before];
                lines.Add(new LogicLineViewModel("-", was.Comment, RungFormatter.Format(was.Text), was.Text)
                {
                    Location = -1,
                    ProjectLocation = row.Before,
                    Change = "Removed",
                    ChangeNote = $"Removed - rung {row.Before.ToString(CultureInfo.InvariantCulture)} in the project",
                });
                continue;
            }

            RungDraft rung = edit[row.After];
            string change = row.Change switch
            {
                RungChange.Added => "Added",
                RungChange.Changed => "Changed",
                _ => row.CommentChanged ? "Changed" : string.Empty,
            };

            string? note = row.Change switch
            {
                RungChange.Added => "Added - not in the project",
                RungChange.Changed => $"Changed - the project has: {original[row.Before].Text}",
                _ => row.CommentChanged ? $"Comment changed - the project has: {original[row.Before].Comment ?? "(none)"}" : null,
            };

            switch (row.Change)
            {
                case RungChange.Added:
                    added++;
                    break;
                case RungChange.Changed:
                    changed++;
                    break;
                default:
                    comments += row.CommentChanged ? 1 : 0;
                    break;
            }

            lines.Add(new LogicLineViewModel(row.After.ToString(CultureInfo.InvariantCulture), rung.Comment, RungFormatter.Format(rung.Text), rung.Text)
            {
                Location = row.After,
                ProjectLocation = row.Before,
                Change = change,
                ChangeNote = note,
            });
        }

        var parts = new List<string>();
        void Part(int n, string what)
        {
            if (n > 0)
            {
                parts.Add($"{n.ToString(CultureInfo.InvariantCulture)} {what}");
            }
        }

        Part(changed, "changed");
        Part(added, "added");
        Part(removed, "removed");
        Part(comments, comments == 1 ? "comment" : "comments");
        summary = parts.Count == 0 ? "no changes yet" : string.Join(", ", parts);
        return lines;
    }

    /// <summary>The built-in shapes plus one per AOI, AOIs winning only for names the table lacks.</summary>
    public static Func<string, InstructionShape?> ShapeLookup(IEnumerable<AoiInfo>? aois)
    {
        var map = new Dictionary<string, InstructionShape>(StringComparer.OrdinalIgnoreCase);
        foreach (AoiInfo aoi in aois ?? [])
        {
            map[aoi.Name] = InstructionShape.ForAoi(aoi.Name, aoi.CallParameters.Select(p => p.Name), aoi.Description);
        }

        return name => InstructionSignatures.Find(name) ?? (map.TryGetValue(name, out InstructionShape? s) ? s : null);
    }
}

/// <summary>
/// One rung or ST line: its number, its comment, its text in coloured runs and - for a rung - the
/// raw neutral text the ladder view draws from.
/// </summary>
public sealed class LogicLineViewModel(string number, string? comment, IReadOnlyList<RungSegment> segments, string? rungText = null)
    : ObservableObject
{
    private bool _highlighted;

    /// <summary>The rung number or ST line index, as findings and cross-references name it.</summary>
    public int Location { get; init; }

    /// <summary>
    /// The rung number in the project this line shows - what findings and cross-references name.
    /// The same as <see cref="Location"/> unless the routine is being edited; -1 for an added rung.
    /// </summary>
    public int ProjectLocation { get; init; } = -1;

    /// <summary>Added, Changed, Removed - how the line differs from the project - or empty.</summary>
    public string Change { get; init; } = string.Empty;

    /// <summary>The tooltip on a changed line: what the project has instead.</summary>
    public string? ChangeNote { get; init; }

    public bool IsRemoved => Change == "Removed";

    /// <summary>The line a finding or cross-reference jumped to.</summary>
    public bool IsHighlighted
    {
        get => _highlighted;
        set => SetProperty(ref _highlighted, value);
    }

    /// <summary>The rung's neutral text, or null for a structured text line.</summary>
    public string? RungText { get; } = rungText;

    public string Number { get; } = number;

    public string Comment { get; } = comment ?? string.Empty;

    public bool HasComment => Comment.Length > 0;

    public IReadOnlyList<SegmentViewModel> Segments { get; } =
        segments.Select(s => new SegmentViewModel(s.Text, s.Kind.ToString())).ToList();
}

/// <summary>A run of text and its kind as a string, for the views' colour triggers.</summary>
public sealed record SegmentViewModel(string Text, string Kind);
