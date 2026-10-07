using LogicControl.Core.Logic;
using LogicControl.Core.Model;

namespace LogicControl.App.ViewModels;

/// <summary>A routine opened in the logic view: one line per rung or ST line.</summary>
public sealed class RoutineViewModel
{
    public RoutineViewModel(RoutineInfo routine, IEnumerable<AoiInfo>? aois = null)
    {
        ArgumentNullException.ThrowIfNull(routine);

        Routine = routine;
        Title = routine.QualifiedName;
        Shapes = ShapeLookup(aois);
        IsLadder = routine.Language == RoutineLanguage.Ladder && !routine.IsProtected;

        if (routine.IsProtected)
        {
            Summary = "Source-protected - the export does not carry its logic.";
        }
        else if (routine.Language == RoutineLanguage.Ladder)
        {
            Lines = routine.Rungs.Select(r => new LogicLineViewModel(
                r.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                r.Comment,
                RungFormatter.Format(r.Text),
                r.Text)).ToList();
            Summary = $"Ladder · {routine.Rungs.Count} rungs";
        }
        else if (routine.Language == RoutineLanguage.StructuredText)
        {
            Lines = routine.StructuredText.Select((text, i) => new LogicLineViewModel(
                i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                comment: null,
                [new RungSegment(text, RungSegmentKind.Operand)])).ToList();
            Summary = $"Structured text · {routine.StructuredText.Count} lines";
        }
        else
        {
            Summary = $"{routine.Language} · {routine.GraphicalElementCount} sheets or steps. "
                + "Function block and SFC are graphical and not drawn yet.";
        }
    }

    public RoutineInfo Routine { get; }

    public string Title { get; }

    public string Summary { get; }

    public IReadOnlyList<LogicLineViewModel> Lines { get; } = [];

    /// <summary>True when the routine can be drawn as ladder - an unprotected RLL routine.</summary>
    public bool IsLadder { get; }

    /// <summary>
    /// How each instruction on these rungs is drawn: the built-in table, then the project's AOIs,
    /// so an AOI call shows a box with its parameter names rather than numbered operands.
    /// </summary>
    public Func<string, InstructionShape?> Shapes { get; }

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
{
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
