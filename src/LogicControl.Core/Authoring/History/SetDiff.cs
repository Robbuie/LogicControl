using System.Globalization;

namespace LogicControl.Core.Authoring.History;

/// <summary>
/// Compares two versions of a development set and says what changed, draft by draft.
///
/// <para>Drafts are matched by kind and name, ignoring case the way Logix does, so a renamed
/// routine reads as one removed and one added - which is also what importing it would do.
/// Inside a routine or AOI the rungs are diffed by their text; a rung whose text is the same but
/// whose comment changed says so on its own line. A run of removed rungs followed by added ones
/// is paired up as changed rungs, because that is how a person edits: they change a rung, they
/// do not delete it and type a new one.</para>
/// </summary>
public static class SetDiff
{
    /// <summary>Unchanged rungs shown either side of a change, for orientation.</summary>
    private const int ContextRungs = 1;

    public static ChangeSet Compare(DevelopmentSet before, DevelopmentSet after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var items = new List<ItemChange>();

        if (!Equal(before.ControllerName, after.ControllerName) || !Equal(before.SoftwareRevision, after.SoftwareRevision))
        {
            var lines = new List<DiffLine>();
            Field(lines, "Controller name", before.ControllerName, after.ControllerName);
            Field(lines, "Studio 5000 version", before.SoftwareRevision, after.SoftwareRevision);
            items.Add(new ItemChange("Settings", "for import files", ItemChangeKind.Changed, lines) { Summary = "changed" });
        }

        Items(items, "Data type", before.DataTypes, after.DataTypes, d => d.Name, Udt);
        Items(items, "Add-On", before.AddOnInstructions, after.AddOnInstructions, a => a.Name, Aoi);
        Items(items, "Program", before.Programs, after.Programs, p => p.Name, Program);
        Items(items, "Routine", before.Routines, after.Routines, r => r.QualifiedName, Routine);
        Items(items, "Tag", before.Tags, after.Tags, t => t.QualifiedName, Tag);
        Items(items, "Module", before.Modules, after.Modules, m => m.Name, Module);

        return new ChangeSet(items);
    }

    // ------------------------------------------------------------------ items

    private static void Items<T>(
        List<ItemChange> into, string what, List<T> before, List<T> after, Func<T, string> key,
        Func<T?, T?, (List<DiffLine> Lines, string Summary, List<int> Touched)> detail)
        where T : class
    {
        Dictionary<string, T> old = before.GroupBy(key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (T item in after)
        {
            string name = key(item);
            if (!seen.Add(name))
            {
                continue;
            }

            if (!old.TryGetValue(name, out T? was))
            {
                (List<DiffLine> lines, string summary, List<int> touched) = detail(null, item);
                into.Add(new ItemChange(what, name, ItemChangeKind.Added, lines) { Summary = summary, TouchedRungs = touched });
                continue;
            }

            (List<DiffLine> changed, string what2, List<int> rungs) = detail(was, item);
            if (changed.Count > 0)
            {
                into.Add(new ItemChange(what, name, ItemChangeKind.Changed, changed) { Summary = what2, TouchedRungs = rungs });
            }
        }

        foreach (T item in before.Where(b => !seen.Contains(key(b))))
        {
            if (seen.Add(key(item)))
            {
                (List<DiffLine> lines, string summary, _) = detail(item, null);
                into.Add(new ItemChange(what, key(item), ItemChangeKind.Removed, lines) { Summary = summary });
            }
        }
    }

    private static (List<DiffLine>, string, List<int>) Udt(UdtDraft? a, UdtDraft? b)
    {
        var lines = new List<DiffLine>();
        Field(lines, "Description", a?.Description, b?.Description);
        string summary = Named(lines, "Members", a?.Members ?? [], b?.Members ?? [], m => m.Name,
            m => $"{m.Name} : {m.DataType}{Dim(m.Dimension)}{Desc(m.Description)}", "member");
        return (lines, summary, []);
    }

    private static (List<DiffLine>, string, List<int>) Aoi(AoiDraft? a, AoiDraft? b)
    {
        var lines = new List<DiffLine>();
        Field(lines, "Revision", a?.Revision, b?.Revision);
        Field(lines, "Description", a?.Description, b?.Description);
        var parts = new List<string>
        {
            Named(lines, "Parameters", a?.Parameters ?? [], b?.Parameters ?? [], p => p.Name,
                p => $"{p.Name} : {p.DataType}, {p.Usage}{(p.IsRequired ? ", required" : string.Empty)}{(p.Visible ? string.Empty : ", hidden")}{Desc(p.Description)}",
                "parameter"),
            Named(lines, "Local tags", a?.LocalTags ?? [], b?.LocalTags ?? [], l => l.Name,
                l => $"{l.Name} : {l.DataType}{Dim(l.Dimension)}{Desc(l.Description)}", "local tag"),
        };

        (string rungs, List<int> touched) = Rungs(lines, a?.Logic ?? [], b?.Logic ?? []);
        parts.Add(rungs);
        return (lines, Join(parts, lines), touched);
    }

    private static (List<DiffLine>, string, List<int>) Program(ProgramDraft? a, ProgramDraft? b)
    {
        var lines = new List<DiffLine>();
        Field(lines, "Description", a?.Description, b?.Description);
        Field(lines, "Main routine", a?.MainRoutineName, b?.MainRoutineName);
        Field(lines, "Task", a?.Task, b?.Task);
        return (lines, lines.Count > 0 && a is not null && b is not null ? "settings changed" : string.Empty, []);
    }

    private static (List<DiffLine>, string, List<int>) Routine(RoutineDraft? a, RoutineDraft? b)
    {
        var lines = new List<DiffLine>();
        Field(lines, "Description", a?.Description, b?.Description);
        (string summary, List<int> touched) = Rungs(lines, a?.Rungs ?? [], b?.Rungs ?? []);
        return (lines, summary.Length > 0 ? summary : lines.Count > 0 ? "description changed" : string.Empty, touched);
    }

    private static (List<DiffLine>, string, List<int>) Tag(TagDraft? a, TagDraft? b)
    {
        var lines = new List<DiffLine>();
        Field(lines, "Data type", a?.AliasFor is null ? a?.DataType : null, b?.AliasFor is null ? b?.DataType : null);
        Field(lines, "Dimensions", a?.Dimensions, b?.Dimensions);
        Field(lines, "Alias for", a?.AliasFor, b?.AliasFor);
        Field(lines, "Description", a?.Description, b?.Description);
        Field(lines, "Constant", a is null ? null : Yes(a.Constant), b is null ? null : Yes(b.Constant));
        Field(lines, "External access", a?.ExternalAccess, b?.ExternalAccess);
        return (lines, string.Empty, []);
    }

    private static (List<DiffLine>, string, List<int>) Module(ModuleDraft? a, ModuleDraft? b)
    {
        var lines = new List<DiffLine>();
        Field(lines, "Parent", a is null ? null : $"{a.ParentModule} port {N(a.ParentPortId)}", b is null ? null : $"{b.ParentModule} port {N(b.ParentPortId)}");
        Field(lines, "IP address", a?.IpAddress, b?.IpAddress);
        Field(lines, "Comm format", a?.Format, b?.Format);
        Field(lines, "Input", a is null ? null : $"instance {N(a.InputInstance)}, {N(a.InputSize)} elements", b is null ? null : $"instance {N(b.InputInstance)}, {N(b.InputSize)} elements");
        Field(lines, "Output", a is null ? null : $"instance {N(a.OutputInstance)}, {N(a.OutputSize)} elements", b is null ? null : $"instance {N(b.OutputInstance)}, {N(b.OutputSize)} elements");
        Field(lines, "Configuration", a is null ? null : $"instance {N(a.ConfigInstance)}, {N(a.ConfigSize)} bytes", b is null ? null : $"instance {N(b.ConfigInstance)}, {N(b.ConfigSize)} bytes");
        Field(lines, "RPI", a is null ? null : $"{a.RpiMs.ToString("0.###", CultureInfo.InvariantCulture)} ms", b is null ? null : $"{b.RpiMs.ToString("0.###", CultureInfo.InvariantCulture)} ms");
        Field(lines, "Unicast", a is null ? null : Yes(a.Unicast), b is null ? null : Yes(b.Unicast));
        Field(lines, "Description", a?.Description, b?.Description);
        return (lines, string.Empty, []);
    }

    // ------------------------------------------------------------------ rungs

    /// <summary>
    /// Diffs two rung lists into <paramref name="lines"/>. Returns a summary ("2 rungs changed, 1
    /// added") and the indexes, in <paramref name="after"/>, of rungs added or changed.
    /// </summary>
    internal static (string Summary, List<int> Touched) Rungs(List<DiffLine> lines, IReadOnlyList<RungDraft> before, IReadOnlyList<RungDraft> after)
    {
        List<(LineChange Change, int Before, int After, bool Edited)> rows = RungComparison.Compare(before, after)
            .Select(r => r.Change switch
            {
                RungChange.Added => (LineChange.Added, -1, r.After, false),
                RungChange.Removed => (LineChange.Removed, r.Before, -1, false),
                RungChange.Changed => (LineChange.Same, r.Before, r.After, true),
                _ => (LineChange.Same, r.Before, r.After, false),
            })
            .ToList();

        bool Interesting(int i)
        {
            (LineChange change, int b, int a, bool edited) = rows[i];
            return edited || change != LineChange.Same || !SameComment(before[b].Comment, after[a].Comment);
        }

        int changed = 0, addedCount = 0, removedCount = 0, comments = 0;
        var touched = new List<int>();
        var body = new List<DiffLine>();
        int hidden = 0;

        for (int i = 0; i < rows.Count; i++)
        {
            (LineChange change, int b, int a, bool edited) = rows[i];
            bool interesting = Interesting(i);
            bool near = Enumerable.Range(Math.Max(0, i - ContextRungs), Math.Min(rows.Count, i + ContextRungs + 1) - Math.Max(0, i - ContextRungs))
                .Any(Interesting);

            if (!interesting && !near)
            {
                hidden++;
                continue;
            }

            if (hidden > 0)
            {
                body.Add(new DiffLine(DiffLineKind.Context, $"   ... {Count(hidden, "unchanged rung")}"));
                hidden = 0;
            }

            switch (change)
            {
                case LineChange.Added:
                    addedCount++;
                    touched.Add(a);
                    body.Add(new DiffLine(DiffLineKind.Added, $"+ Rung {N(a)}  {Comment(after[a].Comment)}{after[a].Text}") { Rung = after[a].Text, RungLabel = $"Rung {N(a)}", RungComment = after[a].Comment });
                    break;

                case LineChange.Removed:
                    removedCount++;
                    body.Add(new DiffLine(DiffLineKind.Removed, $"- Rung {N(b)}  {Comment(before[b].Comment)}{before[b].Text}") { Rung = before[b].Text, RungLabel = $"Rung {N(b)}", RungComment = before[b].Comment });
                    break;

                default:
                    if (edited)
                    {
                        changed++;
                        touched.Add(a);
                        body.Add(new DiffLine(DiffLineKind.Removed, $"- Rung {N(b)}  {before[b].Text}") { Rung = before[b].Text, RungLabel = $"Rung {N(b)} before", RungComment = before[b].Comment });
                        body.Add(new DiffLine(DiffLineKind.Added, $"+ Rung {N(a)}  {after[a].Text}") { Rung = after[a].Text, RungLabel = $"Rung {N(a)} after", RungComment = after[a].Comment });
                    }
                    else
                    {
                        body.Add(new DiffLine(DiffLineKind.Context, $"  Rung {N(a)}  {after[a].Text}") { Rung = after[a].Text, RungLabel = $"Rung {N(a)}", RungComment = after[a].Comment });
                    }

                    if (!SameComment(before[b].Comment, after[a].Comment))
                    {
                        if (!edited)
                        {
                            comments++;
                            touched.Add(a);
                        }

                        body.Add(new DiffLine(DiffLineKind.Changed, $"~ Rung {N(a)} comment: {Quote(before[b].Comment)} → {Quote(after[a].Comment)}"));
                    }

                    break;
            }
        }

        if (hidden > 0)
        {
            body.Add(new DiffLine(DiffLineKind.Context, $"   ... {Count(hidden, "unchanged rung")}"));
        }

        if (body.Count > 0 && (changed + addedCount + removedCount + comments) > 0)
        {
            lines.Add(new DiffLine(DiffLineKind.Heading, "Rungs"));
            lines.AddRange(body);
        }

        var parts = new List<string>();
        if (before.Count == 0 && after.Count > 0)
        {
            parts.Add(Count(after.Count, "rung"));
        }
        else if (after.Count == 0 && before.Count > 0)
        {
            parts.Add(Count(before.Count, "rung"));
        }
        else
        {
            if (changed > 0)
            {
                parts.Add($"{Count(changed, "rung")} changed");
            }

            if (addedCount > 0)
            {
                parts.Add($"{Count(addedCount, "rung")} added");
            }

            if (removedCount > 0)
            {
                parts.Add($"{Count(removedCount, "rung")} removed");
            }

            if (comments > 0)
            {
                parts.Add($"{Count(comments, "comment")} changed");
            }
        }

        return (string.Join(", ", parts), touched);
    }

    // ------------------------------------------------------------------ fields and named lists

    private static void Field(List<DiffLine> lines, string label, string? before, string? after)
    {
        if (Equal(before, after))
        {
            return;
        }

        if (string.IsNullOrEmpty(before))
        {
            lines.Add(new DiffLine(DiffLineKind.Added, $"+ {label}: {after}"));
        }
        else if (string.IsNullOrEmpty(after))
        {
            lines.Add(new DiffLine(DiffLineKind.Removed, $"- {label}: {before}"));
        }
        else
        {
            lines.Add(new DiffLine(DiffLineKind.Changed, $"~ {label}: {before} → {after}"));
        }
    }

    /// <summary>Members, parameters, local tags: added, removed and changed by name, then order.</summary>
    private static string Named<T>(
        List<DiffLine> lines, string heading, IReadOnlyList<T> before, IReadOnlyList<T> after,
        Func<T, string> key, Func<T, string> describe, string noun)
    {
        Dictionary<string, T> old = before.GroupBy(key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var now = new HashSet<string>(after.Select(key), StringComparer.OrdinalIgnoreCase);
        var body = new List<DiffLine>();
        int added = 0, removed = 0, changed = 0;

        foreach (T item in after)
        {
            if (!old.TryGetValue(key(item), out T? was))
            {
                added++;
                body.Add(new DiffLine(DiffLineKind.Added, $"+ {describe(item)}"));
            }
            else if (!string.Equals(describe(was), describe(item), StringComparison.Ordinal))
            {
                changed++;
                body.Add(new DiffLine(DiffLineKind.Changed, $"~ {describe(was)} → {describe(item)}"));
            }
        }

        foreach (T item in before.Where(b => !now.Contains(key(b))))
        {
            removed++;
            body.Add(new DiffLine(DiffLineKind.Removed, $"- {describe(item)}"));
        }

        List<string> keptBefore = before.Select(key).Where(now.Contains).ToList();
        List<string> keptAfter = after.Select(key).Where(k => old.ContainsKey(k)).ToList();
        bool reordered = !keptBefore.SequenceEqual(keptAfter, StringComparer.OrdinalIgnoreCase);
        if (reordered)
        {
            body.Add(new DiffLine(DiffLineKind.Changed, $"~ Order: {string.Join(", ", keptAfter)}"));
        }

        if (body.Count == 0)
        {
            return string.Empty;
        }

        lines.Add(new DiffLine(DiffLineKind.Heading, heading));
        lines.AddRange(body);

        if (before.Count == 0)
        {
            return Count(after.Count, noun);
        }

        if (after.Count == 0)
        {
            return Count(before.Count, noun);
        }

        var parts = new List<string>();
        if (added > 0)
        {
            parts.Add($"{Count(added, noun)} added");
        }

        if (removed > 0)
        {
            parts.Add($"{Count(removed, noun)} removed");
        }

        if (changed > 0)
        {
            parts.Add($"{Count(changed, noun)} changed");
        }

        if (reordered)
        {
            parts.Add($"{noun}s reordered");
        }

        return string.Join(", ", parts);
    }

    // ------------------------------------------------------------------ text

    private static string Join(IEnumerable<string> parts, List<DiffLine> lines)
    {
        string joined = string.Join("; ", parts.Where(p => p.Length > 0));
        return joined.Length > 0 ? joined : lines.Count > 0 ? "details changed" : string.Empty;
    }

    private static bool Equal(string? a, string? b) =>
        string.Equals(string.IsNullOrEmpty(a) ? null : a, string.IsNullOrEmpty(b) ? null : b, StringComparison.Ordinal);

    private static bool SameComment(string? a, string? b) => RungComparison.SameComment(a, b);

    private static string Comment(string? comment) =>
        string.IsNullOrWhiteSpace(comment) ? string.Empty : $"// {OneLine(comment)}  ";

    private static string Quote(string? text) => string.IsNullOrEmpty(text) ? "(none)" : $"\"{OneLine(text)}\"";

    private static string OneLine(string text) => text.ReplaceLineEndings(" ").Trim();

    private static string Dim(int dimension) => dimension > 0 ? $"[{N(dimension)}]" : string.Empty;

    private static string Desc(string? description) => string.IsNullOrWhiteSpace(description) ? string.Empty : $"  // {OneLine(description)}";

    private static string Yes(bool value) => value ? "yes" : "no";

    private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);

    private static string Count(int n, string noun) => $"{N(n)} {noun}{(n == 1 ? string.Empty : "s")}";
}
