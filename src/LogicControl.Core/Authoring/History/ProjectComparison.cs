using System.Globalization;
using LogicControl.Core.Model;

namespace LogicControl.Core.Authoring.History;

/// <summary>
/// Two exports side by side: what differs between them, item by item - data types, Add-Ons,
/// programs, routines (ladder rung by rung, structured text line by line), tags, modules and
/// tasks. The same <see cref="ChangeSet"/> the history uses, so the same viewer shows it, with
/// rungs drawn as ladder.
///
/// <para><b>Direction.</b> <see cref="Compare"/> reads "from <c>before</c> to <c>after</c>": an
/// item only in <c>after</c> is Added, one only in <c>before</c> is Removed. Which export is which
/// is the caller's choice - an older backup against the running project, or the other way - and
/// swapping them reverses every line.</para>
///
/// <para><b>Scope.</b> The whole controller, the controller-scoped part (data types, Add-Ons,
/// controller tags, modules, tasks), or one program (its settings, routines and tags).</para>
/// </summary>
public static class ProjectComparison
{
    /// <summary>Everything in both exports.</summary>
    public const string All = "";

    /// <summary>Data types, Add-Ons, controller tags, modules and tasks.</summary>
    public const string ControllerScope = "(controller)";

    private static readonly string[] Order = ["Data type", "Add-On", "Module", "Task", "Program", "Routine", "Tag"];

    public static ChangeSet Compare(PlcProject before, PlcProject after, string scope = All)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        scope ??= All;

        bool everything = scope.Length == 0;
        bool controller = everything || scope == ControllerScope;
        string? program = everything || scope == ControllerScope ? null : scope;

        var items = new List<ItemChange>();

        // Ladder, data types, Add-Ons and program settings: the drafts' own diff.
        DevelopmentSet a = Drafts(before, controller, program, everything);
        DevelopmentSet b = Drafts(after, controller, program, everything);
        a.ControllerName = b.ControllerName = string.Empty;
        a.SoftwareRevision = b.SoftwareRevision = string.Empty;
        items.AddRange(SetDiff.Compare(a, b).Items);

        // What drafts do not model: structured text, the full tag record, any module, tasks.
        StructuredText(before, after, program, everything, items);
        Tags(before, after, controller, program, everything, items);
        if (controller)
        {
            Modules(before, after, items);
            Tasks(before, after, items);
        }

        return new ChangeSet(items
            .OrderBy(i => Array.IndexOf(Order, i.What) is var o && o < 0 ? Order.Length : o)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList());
    }

    /// <summary>
    /// Two routines that need not share a name - "this routine against that one". Ladder against
    /// ladder rung by rung, structured text line by line.
    /// </summary>
    public static ItemChange CompareRoutines(RoutineInfo before, RoutineInfo after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        string name = string.Equals(before.QualifiedName, after.QualifiedName, StringComparison.OrdinalIgnoreCase)
            ? after.QualifiedName
            : $"{before.QualifiedName} → {after.QualifiedName}";

        var lines = new List<DiffLine>();
        string summary;
        if (before.Language == RoutineLanguage.StructuredText || after.Language == RoutineLanguage.StructuredText)
        {
            summary = TextLines(lines, before.StructuredText, after.StructuredText);
        }
        else
        {
            (summary, _) = SetDiff.Rungs(lines,
                before.Rungs.Select(r => new RungDraft(r.Text, r.Comment)).ToList(),
                after.Rungs.Select(r => new RungDraft(r.Text, r.Comment)).ToList());
        }

        if (!string.Equals(before.Description, after.Description, StringComparison.Ordinal))
        {
            lines.Insert(0, new DiffLine(DiffLineKind.Changed, $"~ Description: {before.Description ?? "(none)"} → {after.Description ?? "(none)"}"));
        }

        return new ItemChange("Routine", name, lines.Count == 0 ? ItemChangeKind.Changed : ItemChangeKind.Changed, lines)
        {
            Summary = lines.Count == 0 ? "identical" : summary.Length > 0 ? summary : "description changed",
        };
    }

    /// <summary>The scopes a comparison can be narrowed to: everything, controller scope, each program in either export.</summary>
    public static IReadOnlyList<string> Scopes(PlcProject a, PlcProject b) =>
        [All, ControllerScope, .. a.Programs.Select(p => p.Name).Concat(b.Programs.Select(p => p.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];

    // ------------------------------------------------------------------ parts

    private static DevelopmentSet Drafts(PlcProject p, bool controller, string? program, bool everything)
    {
        var set = new DevelopmentSet();
        if (controller)
        {
            set.DataTypes.AddRange(p.DataTypes.Select(DraftsFromProject.DataType));
            set.AddOnInstructions.AddRange(p.AddOnInstructions.Where(a => !a.IsProtected).Select(DraftsFromProject.Aoi));
        }

        foreach (ProgramInfo prog in p.Programs.Where(x => everything || Same(x.Name, program)))
        {
            set.Programs.Add(DraftsFromProject.Program(prog, p));
            set.Routines.AddRange(prog.Routines
                .Where(r => r.Language == RoutineLanguage.Ladder && !r.IsProtected)
                .Select(DraftsFromProject.Routine));
        }

        return set;
    }

    private static void StructuredText(PlcProject before, PlcProject after, string? program, bool everything, List<ItemChange> items)
    {
        Dictionary<string, RoutineInfo> Of(PlcProject p) => p.Programs
            .Where(x => everything || Same(x.Name, program))
            .SelectMany(x => x.Routines)
            .Where(r => r.Language == RoutineLanguage.StructuredText && !r.IsProtected)
            .GroupBy(r => r.QualifiedName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        Dictionary<string, RoutineInfo> a = Of(before);
        Dictionary<string, RoutineInfo> b = Of(after);

        foreach ((string name, RoutineInfo routine) in b)
        {
            var lines = new List<DiffLine>();
            if (!a.TryGetValue(name, out RoutineInfo? was))
            {
                lines.AddRange(routine.StructuredText.Select(l => new DiffLine(DiffLineKind.Added, "+ " + l)));
                items.Add(new ItemChange("Routine", name, ItemChangeKind.Added, lines) { Summary = $"{N(routine.StructuredText.Count)} ST lines" });
                continue;
            }

            string summary = TextLines(lines, was.StructuredText, routine.StructuredText);
            if (lines.Count > 0)
            {
                items.Add(new ItemChange("Routine", name, ItemChangeKind.Changed, lines) { Summary = summary });
            }
        }

        foreach ((string name, RoutineInfo routine) in a.Where(p => !b.ContainsKey(p.Key)))
        {
            items.Add(new ItemChange("Routine", name, ItemChangeKind.Removed,
                routine.StructuredText.Select(l => new DiffLine(DiffLineKind.Removed, "- " + l)).ToList())
            { Summary = $"{N(routine.StructuredText.Count)} ST lines" });
        }
    }

    /// <summary>Structured text, line by line, with a line of context around each change.</summary>
    private static string TextLines(List<DiffLine> lines, IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        IReadOnlyList<LineStep> steps = LineDiff.Compute(
            before.Select(l => l.Trim()).ToList(), after.Select(l => l.Trim()).ToList());

        int added = 0, removed = 0, hidden = 0;
        for (int i = 0; i < steps.Count; i++)
        {
            LineStep s = steps[i];
            bool near = steps.Skip(Math.Max(0, i - 1)).Take(3).Any(x => x.Change != LineChange.Same);
            if (s.Change == LineChange.Same && !near)
            {
                hidden++;
                continue;
            }

            if (hidden > 0)
            {
                lines.Add(new DiffLine(DiffLineKind.Context, $"   ... {N(hidden)} unchanged lines"));
                hidden = 0;
            }

            switch (s.Change)
            {
                case LineChange.Added:
                    added++;
                    lines.Add(new DiffLine(DiffLineKind.Added, $"+ {N(s.After)}: {after[s.After]}"));
                    break;
                case LineChange.Removed:
                    removed++;
                    lines.Add(new DiffLine(DiffLineKind.Removed, $"- {N(s.Before)}: {before[s.Before]}"));
                    break;
                default:
                    lines.Add(new DiffLine(DiffLineKind.Context, $"  {N(s.After)}: {after[s.After]}"));
                    break;
            }
        }

        if (added + removed == 0)
        {
            lines.Clear();
            return string.Empty;
        }

        if (hidden > 0)
        {
            lines.Add(new DiffLine(DiffLineKind.Context, $"   ... {N(hidden)} unchanged lines"));
        }

        lines.Insert(0, new DiffLine(DiffLineKind.Heading, "Structured text"));
        return string.Join(", ", new[] { added > 0 ? $"{N(added)} lines added" : null, removed > 0 ? $"{N(removed)} lines removed" : null }.Where(x => x is not null));
    }

    private static void Tags(PlcProject before, PlcProject after, bool controller, string? program, bool everything, List<ItemChange> items)
    {
        bool InScope(TagInfo t) => everything || (t.Scope is null ? controller : Same(t.Scope, program));

        Compare(items, "Tag",
            before.AllTags.Where(InScope).ToList(), after.AllTags.Where(InScope).ToList(), t => t.QualifiedName,
            t =>
            [
                ("Kind", t.Kind.ToString()),
                ("Data type", t.DataType),
                ("Dimensions", t.Dimensions),
                ("Alias for", t.AliasFor),
                ("Description", t.Description),
                ("External access", t.ExternalAccess),
                ("Constant", t.Constant ? "yes" : null),
                ("Producer", t.Consume?.Producer),
                ("Remote tag", t.Consume?.RemoteTag),
                ("RPI", t.Consume?.RpiMs is { } r ? $"{Ms(r)} ms" : null),
                ("Consumers allowed", t.ProduceCount?.ToString(CultureInfo.InvariantCulture)),
                ("Message", t.Message?.MessageType),
                ("Message path", t.Message?.ConnectionPath),
                ("Message remote", t.Message?.RemoteElement),
                ("Message local", t.Message?.LocalElement),
            ],
            t => $"{t.DataType ?? (t.AliasFor is null ? "?" : "alias of " + t.AliasFor)}{(t.Dimensions is { } d ? $"[{d}]" : string.Empty)}");
    }

    private static void Modules(PlcProject before, PlcProject after, List<ItemChange> items) =>
        Compare(items, "Module",
            before.Modules.ToList(), after.Modules.ToList(), m => m.Name,
            m =>
            [
                ("Catalog", m.CatalogNumber),
                ("Revision", m.Revision),
                ("Parent", m.ParentModule is null ? null : $"{m.ParentModule} port {m.ParentPortId?.ToString(CultureInfo.InvariantCulture)}"),
                ("Address", m.IpAddress ?? m.Slot?.ToString(CultureInfo.InvariantCulture)),
                ("RPI", m.Connections.Min(c => c.RpiMs) is { } r ? $"{Ms(r)} ms" : null),
                ("Connection", string.Join("; ", m.Connections.Select(c => $"{c.Type} in {c.InputSize} out {c.OutputSize}"))),
                ("Keying", m.Keying),
                ("Inhibited", m.Inhibited ? "yes" : null),
                ("Major fault on connection loss", m.MajorFaultOnConnectionFailure ? "yes" : null),
                ("Description", m.Description),
            ],
            m => $"{m.CatalogNumber} {m.IpAddress}".Trim());

    private static void Tasks(PlcProject before, PlcProject after, List<ItemChange> items) =>
        Compare(items, "Task",
            before.Tasks.ToList(), after.Tasks.ToList(), t => t.Name,
            t =>
            [
                ("Type", t.Type),
                ("Rate", t.RateMs is { } r ? $"{Ms(r)} ms" : null),
                ("Priority", t.Priority?.ToString(CultureInfo.InvariantCulture)),
                ("Watchdog", t.WatchdogMs is { } w ? $"{w.ToString(CultureInfo.InvariantCulture)} ms" : null),
                ("Inhibited", t.InhibitTask ? "yes" : null),
                ("Programs", string.Join(", ", t.ScheduledPrograms)),
            ],
            t => $"{t.Type} {(t.RateMs is { } r ? Ms(r) + " ms" : string.Empty)}".Trim());

    /// <summary>Items by name: added, removed, and field by field for the ones in both.</summary>
    private static void Compare<T>(
        List<ItemChange> items, string what, List<T> before, List<T> after, Func<T, string> key,
        Func<T, (string Label, string? Value)[]> fields, Func<T, string> brief)
    {
        Dictionary<string, T> a = before.GroupBy(key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        Dictionary<string, T> b = after.GroupBy(key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach ((string name, T item) in b)
        {
            if (!a.TryGetValue(name, out T? was))
            {
                items.Add(new ItemChange(what, name, ItemChangeKind.Added,
                    fields(item).Where(f => !string.IsNullOrEmpty(f.Value)).Select(f => new DiffLine(DiffLineKind.Added, $"+ {f.Label}: {f.Value}")).ToList())
                { Summary = brief(item) });
                continue;
            }

            var lines = new List<DiffLine>();
            (string Label, string? Value)[] old = fields(was);
            (string Label, string? Value)[] now = fields(item);
            for (int i = 0; i < now.Length; i++)
            {
                string? x = string.IsNullOrEmpty(old[i].Value) ? null : old[i].Value;
                string? y = string.IsNullOrEmpty(now[i].Value) ? null : now[i].Value;
                if (string.Equals(x, y, StringComparison.Ordinal))
                {
                    continue;
                }

                lines.Add(x is null ? new DiffLine(DiffLineKind.Added, $"+ {now[i].Label}: {y}")
                    : y is null ? new DiffLine(DiffLineKind.Removed, $"- {now[i].Label}: {x}")
                    : new DiffLine(DiffLineKind.Changed, $"~ {now[i].Label}: {x} → {y}"));
            }

            if (lines.Count > 0)
            {
                items.Add(new ItemChange(what, name, ItemChangeKind.Changed, lines)
                {
                    Summary = string.Join(", ", lines.Select(l => l.Text[2..l.Text.IndexOf(':', StringComparison.Ordinal)].ToLowerInvariant())) + " changed",
                });
            }
        }

        foreach ((string name, T item) in a.Where(p => !b.ContainsKey(p.Key)))
        {
            items.Add(new ItemChange(what, name, ItemChangeKind.Removed,
                fields(item).Where(f => !string.IsNullOrEmpty(f.Value)).Select(f => new DiffLine(DiffLineKind.Removed, $"- {f.Label}: {f.Value}")).ToList())
            { Summary = brief(item) });
        }
    }

    /// <summary>Plain text of a change, for Claude's tools and the clipboard.</summary>
    public static string Describe(ItemChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{change.Title} - {ChangeSet.Verb(change.Kind)}{(change.Summary.Length > 0 ? ": " + change.Summary : string.Empty)}");
        foreach (DiffLine line in change.Lines)
        {
            sb.AppendLine(line.Kind == DiffLineKind.Heading ? $"[{line.Text}]" : line.Text);
        }

        return sb.ToString();
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);

    private static string Ms(double ms) => ms.ToString("0.###", CultureInfo.InvariantCulture);
}
