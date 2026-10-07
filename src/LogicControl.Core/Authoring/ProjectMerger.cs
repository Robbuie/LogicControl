using System.Xml;
using System.Xml.Linq;
using LogicControl.Core.L5x;

namespace LogicControl.Core.Authoring;

/// <summary>
/// Writes drafts into a whole-controller L5X and saves the result as a new file.
///
/// <para>The other way of getting work into Studio 5000: rather than importing pieces into an open
/// project, open this file in Studio 5000 (File > Open, pick the .L5X) and it builds a new .ACD
/// from it. Everything this tool does not understand is kept exactly as it was - the merge edits
/// the original XML in place, it does not regenerate the project from the model - so modules,
/// motion groups, safety signatures and tag values all survive.</para>
///
/// <para>A draft with the same name as something in the file replaces it; anything new is added.
/// The original file is never written: <see cref="Merge"/> refuses to save over the path it read.</para>
/// </summary>
public static class ProjectMerger
{
    public static MergeReport Merge(string sourcePath, DevelopmentSet set, string outputPath, DateTime? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(set);

        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Save the merged project under a new name - LogicControl never overwrites the export it read.");
        }

        XDocument document = LoadPreservingFormat(sourcePath);
        MergeReport report = Merge(document, set, now);
        L5xWriter.Save(document, outputPath);
        return report;
    }

    /// <summary>Merges into a loaded document. The document is changed in place.</summary>
    public static MergeReport Merge(XDocument document, DevelopmentSet set, DateTime? now = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(set);

        XElement root = document.Root ?? throw new L5xFormatException("The file is empty.");
        if (!string.Equals((string?)root.Attribute("TargetType"), "Controller", StringComparison.OrdinalIgnoreCase))
        {
            throw new L5xFormatException(
                "Merging needs a whole-controller export. This file is a "
                + $"{(string?)root.Attribute("TargetType") ?? "partial"} export - export the full project "
                + "(File > Save As > L5X), or use Export import files instead.");
        }

        XElement controller = root.Element("Controller") ?? throw new L5xFormatException("The export has no <Controller> element.");
        var report = new MergeReport();
        DateTime stamp = now ?? DateTime.UtcNow;

        // Containers in the order Studio 5000 writes them, created only when something goes in.
        if (set.DataTypes.Count > 0)
        {
            XElement dataTypes = Container(controller, "DataTypes");
            foreach (UdtDraft udt in set.DataTypes)
            {
                Upsert(dataTypes, "DataType", udt.Name, L5xWriter.DataType(udt), report, "Data type");
            }

            SortByDependency(dataTypes, "DataType", e => e.Element("Members")?.Elements("Member").Select(m => (string?)m.Attribute("DataType")));
        }

        if (set.AddOnInstructions.Count > 0)
        {
            XElement aois = Container(controller, "AddOnInstructionDefinitions");
            foreach (AoiDraft aoi in set.AddOnInstructions)
            {
                Upsert(aois, "AddOnInstructionDefinition", aoi.Name, L5xWriter.Aoi(aoi, set.SoftwareRevision, stamp), report, "Add-On");
            }

            SortByDependency(aois, "AddOnInstructionDefinition", e =>
                e.Descendants("Text").SelectMany(t => Logic.RungParser.Parse(t.Value)).Select(i => (string?)i.Mnemonic)
                    .Concat(e.Element("Parameters")?.Elements("Parameter").Select(p => (string?)p.Attribute("DataType")) ?? [])
                    .Concat(e.Element("LocalTags")?.Elements("LocalTag").Select(p => (string?)p.Attribute("DataType")) ?? []));
        }

        if (set.Modules.Count > 0)
        {
            XElement modules = Container(controller, "Modules");
            foreach (ModuleDraft module in set.Modules)
            {
                XElement? existing = Find(modules, "Module", module.Name);
                if (existing is not null && !string.Equals((string?)existing.Attribute("CatalogNumber"), "ETHERNET-MODULE", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Module {module.Name}: the project has a {(string?)existing.Attribute("CatalogNumber")} by that name. "
                        + "Only Generic Ethernet modules are written; rename the draft.");
                }

                if (Find(modules, "Module", module.ParentModule) is null)
                {
                    throw new InvalidOperationException($"Module {module.Name}: its parent {module.ParentModule} is not in the I/O tree.");
                }

                // A new module is appended: its parent is already above it, which Studio 5000 needs.
                Upsert(modules, "Module", module.Name, L5xWriter.Module(module), report, "Module");
            }

            report.Notes.Add("Generic Ethernet modules: check the comm format and sizes in the module's properties after opening the copy.");
        }

        XElement tags = Container(controller, "Tags");
        XElement programs = Container(controller, "Programs");

        foreach (TagDraft tag in set.Tags.Where(t => t.Program is null))
        {
            Upsert(tags, "Tag", tag.Name, L5xWriter.Tag(tag), report, "Tag");
        }

        foreach (ProgramDraft program in set.Programs)
        {
            XElement? existing = Find(programs, "Program", program.Name);
            if (existing is null)
            {
                programs.Add(L5xWriter.Program(program, [], []));
                report.Added.Add($"Program {program.Name}");
                Schedule(controller, program, report);
            }
            else
            {
                if (program.MainRoutineName is { Length: > 0 } main)
                {
                    existing.SetAttributeValue("MainRoutineName", main);
                }

                report.Replaced.Add($"Program {program.Name} (settings only - its other routines and tags are kept)");
            }
        }

        foreach (TagDraft tag in set.Tags.Where(t => t.Program is not null))
        {
            XElement program = Find(programs, "Program", tag.Program!)
                ?? throw new InvalidOperationException($"Tag {tag.QualifiedName}: the program is not in the file or the drafts.");
            Upsert(Container(program, "Tags"), "Tag", tag.Name, L5xWriter.Tag(tag), report, $"Tag {tag.Program}.");
        }

        foreach (RoutineDraft routine in set.Routines)
        {
            XElement program = Find(programs, "Program", routine.Program)
                ?? throw new InvalidOperationException($"Routine {routine.QualifiedName}: the program is not in the file or the drafts.");
            Upsert(Container(program, "Routines"), "Routine", routine.Name, L5xWriter.Routine(routine), report, $"Routine {routine.Program}/");

            if (program.Attribute("MainRoutineName") is null)
            {
                program.SetAttributeValue("MainRoutineName", routine.Name);
            }
        }

        return report;
    }

    private static XDocument LoadPreservingFormat(string path)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(path, settings);
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static void Upsert(XElement container, string element, string name, XElement replacement, MergeReport report, string label)
    {
        string what = label.EndsWith('.') || label.EndsWith('/') ? label + name : $"{label} {name}";
        XElement? existing = Find(container, element, name);
        if (existing is null)
        {
            container.Add(replacement);
            report.Added.Add(what);
        }
        else
        {
            existing.ReplaceWith(replacement);
            report.Replaced.Add(what);
        }
    }

    private static XElement? Find(XElement container, string element, string name) =>
        container.Elements(element).FirstOrDefault(e => string.Equals((string?)e.Attribute("Name"), name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The named child, created in Studio 5000's order when it is missing.</summary>
    private static XElement Container(XElement parent, string name)
    {
        if (parent.Element(name) is { } existing)
        {
            return existing;
        }

        string[] order = parent.Name.LocalName == "Controller"
            ? ["RedundancyInfo", "Security", "SafetyInfo", "DataTypes", "Modules", "AddOnInstructionDefinitions", "Tags", "Programs", "Tasks"]
            : ["Description", "Tags", "Routines"];

        var created = new XElement(name);
        int rank = Array.IndexOf(order, name);
        XElement? after = parent.Elements().LastOrDefault(e => Array.IndexOf(order, e.Name.LocalName) is int r && r >= 0 && r < rank);

        if (after is not null)
        {
            after.AddAfterSelf(created);
        }
        else
        {
            parent.AddFirst(created);
        }

        return created;
    }

    private static void Schedule(XElement controller, ProgramDraft program, MergeReport report)
    {
        if (program.Task is not { Length: > 0 } taskName)
        {
            report.Notes.Add($"Program {program.Name} is not scheduled in a task - add it in Studio 5000, or set a task on the draft.");
            return;
        }

        XElement? task = controller.Element("Tasks")?.Elements("Task")
            .FirstOrDefault(t => string.Equals((string?)t.Attribute("Name"), taskName, StringComparison.OrdinalIgnoreCase));

        if (task is null)
        {
            report.Notes.Add($"Task {taskName} is not in the project; program {program.Name} is unscheduled.");
            return;
        }

        Container(task, "ScheduledPrograms").Add(new XElement("ScheduledProgram", new XAttribute("Name", program.Name)));
    }

    /// <summary>Reorders a container so everything a child depends on comes before it. Stable otherwise.</summary>
    private static void SortByDependency(XElement container, string element, Func<XElement, IEnumerable<string?>?> uses)
    {
        List<XElement> items = container.Elements(element).ToList();
        var byName = items
            .GroupBy(e => (string?)e.Attribute("Name") ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var ordered = new List<XElement>();
        var seen = new HashSet<XElement>();

        void Visit(XElement e)
        {
            if (!seen.Add(e))
            {
                return;
            }

            foreach (string? name in uses(e) ?? [])
            {
                if (name is not null && byName.TryGetValue(name, out XElement? dep) && !ReferenceEquals(dep, e))
                {
                    Visit(dep);
                }
            }

            ordered.Add(e);
        }

        foreach (XElement e in items)
        {
            Visit(e);
        }

        if (ordered.SequenceEqual(items))
        {
            return;
        }

        foreach (XElement e in items)
        {
            e.Remove();
        }

        container.Add(ordered);
    }
}

/// <summary>What a merge did, for the confirmation the app shows.</summary>
public sealed class MergeReport
{
    public List<string> Added { get; } = [];

    public List<string> Replaced { get; } = [];

    public List<string> Notes { get; } = [];

    public override string ToString()
    {
        var lines = new List<string>();
        if (Added.Count > 0)
        {
            lines.Add($"Added: {string.Join(", ", Added)}");
        }

        if (Replaced.Count > 0)
        {
            lines.Add($"Replaced: {string.Join(", ", Replaced)}");
        }

        lines.AddRange(Notes);
        return string.Join(Environment.NewLine, lines);
    }
}
