using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LogicControl.Core.Analysis;
using LogicControl.Core.Authoring;
using LogicControl.Core.Authoring.History;
using LogicControl.Core.Logic;
using LogicControl.Core.Model;

namespace LogicControl.Core.Assistant;

/// <summary>
/// What an AI can do with LogicControl: read the open project the way an engineer would - routines,
/// tags, cross-references, hardware, communications, findings - and write drafts into the Develop
/// set, where the same checker the editor uses says at once what is wrong with them.
///
/// <para>One table of tools, two front ends: the assistant panel in the app sends them to Claude
/// through the API (<see cref="ConversationSession"/>), and LogicControl.Mcp offers them to Claude
/// in VS Code or the desktop app. Both get exactly the same names, schemas and answers.</para>
///
/// <para>The boundary is the point. <b>Nothing here can reach a controller</b>, and nothing writes
/// a file: the only thing a tool can change is the development set, which a person reviews,
/// exports and imports through Studio 5000. Read tools answer in compact text rather than JSON,
/// because that is what a model reads best and it costs a third of the tokens; every answer is
/// capped so one careless "list everything" cannot fill the context.</para>
/// </summary>
public sealed class LogicTools(IToolHost host)
{
    /// <summary>Longest answer a tool returns. Past this it says what it left out and how to narrow it.</summary>
    public const int MaxResultChars = 24_000;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public IReadOnlyList<ToolDefinition> Definitions { get; } = BuildDefinitions(host.CanOpenProjects);

    /// <summary>Runs a tool. Never throws: a bad input or a missing project is an error result the model can read and correct.</summary>
    public ToolResult Execute(string name, JsonElement input)
    {
        try
        {
            ToolResult result = name switch
            {
                "open_project" => OpenProject(input),
                "project_overview" => Overview(),
                "list_routines" => ListRoutines(input),
                "read_routine" => ReadRoutine(input),
                "find_tags" => FindTags(input),
                "tag_references" => TagReferences(input),
                "get_data_type" => GetDataType(input),
                "list_findings" => ListFindings(input),
                "list_communications" => ListCommunications(input),
                "list_hardware" => ListHardware(input),
                "instruction_help" => InstructionHelp(input),
                "check_rungs" => CheckRungs(input),
                "draft_data_type" => DraftDataType(input),
                "draft_tags" => DraftTags(input),
                "draft_routine" => DraftRoutine(input),
                "draft_aoi" => DraftAoi(input),
                "list_drafts" => ListDrafts(),
                "remove_draft" => RemoveDraft(input),
                "open_comparison" => OpenComparison(input),
                "compare_summary" => CompareSummary(input),
                "compare_item" => CompareItem(input),
                "read_other_routine" => ReadOtherRoutine(input),
                _ => ToolResult.Fail($"There is no tool called '{name}'."),
            };

            return result.Text.Length <= MaxResultChars
                ? result
                : result with { Text = result.Text[..MaxResultChars] + "\n... (cut: the answer was longer than the tool returns. Narrow the request - a program, a filter, or a smaller range.)" };
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return ToolResult.Fail($"{name}: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ reading

    private ToolResult OpenProject(JsonElement input)
    {
        string path = Required(input, "path");
        string? problem = host.OpenProject(path);
        return problem is null ? Overview() : ToolResult.Fail(problem);
    }

    private ToolResult Overview()
    {
        ProjectAnalysis a = Analysis();
        PlcProject p = a.Project;
        var sb = new StringBuilder();

        sb.AppendLine(CultureInfo.InvariantCulture, $"Controller {p.Controller.Name} - {p.Controller.ProcessorType ?? "unknown type"}, firmware {p.Controller.MajorRevision}.{p.Controller.MinorRevision}{(p.Controller.IsSafety ? ", GuardLogix (safety)" : string.Empty)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Export: {p.TargetType} '{p.TargetName}', Studio 5000 v{p.SoftwareRevision}, file {Path.GetFileName(p.SourcePath)}");
        if (p.Controller.Description is { Length: > 0 } d)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Description: {d}");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"{p.Modules.Count} modules, {a.TagCount} tags, {p.DataTypes.Count} UDTs, {p.AddOnInstructions.Count} AOIs, {a.RungCount} rungs, {a.Communications.Count} communication links, {a.Findings.Count} findings ({a.Findings.Count(f => f.Severity == FindingSeverity.Error)} errors).");
        sb.AppendLine();
        sb.AppendLine("Tasks:");
        foreach (TaskInfo t in p.Tasks)
        {
            string rate = t.RateMs is double r ? $" every {r.ToString(CultureInfo.InvariantCulture)} ms" : string.Empty;
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {t.Name} ({t.Type}{rate}, priority {t.Priority}){(t.InhibitTask ? " INHIBITED" : string.Empty)}: {string.Join(", ", t.ScheduledPrograms)}");
        }

        sb.AppendLine("Programs:");
        foreach (ProgramInfo prog in p.Programs)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {prog.Name}{(prog.Disabled ? " (disabled)" : string.Empty)} - main {prog.MainRoutineName ?? "(none)"}; routines: {string.Join(", ", prog.Routines.Select(RoutineLabel))}; {prog.Tags.Count} program tags");
        }

        if (p.DataTypes.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"UDTs: {string.Join(", ", p.DataTypes.Select(t => t.Name))}");
        }

        if (p.AddOnInstructions.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"AOIs: {string.Join(", ", p.AddOnInstructions.Select(x => x.Name + (x.IsProtected ? " (protected)" : string.Empty)))}");
        }

        return ToolResult.Ok(sb.ToString());
    }

    private ToolResult ListRoutines(JsonElement input)
    {
        PlcProject p = Analysis().Project;
        string? program = Optional(input, "program");
        var sb = new StringBuilder();

        foreach (RoutineInfo r in p.AllRoutines.Where(r => program is null || Same(r.Owner, program)))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{r.QualifiedName} [{LanguageText(r)}]{(r.Description is { Length: > 0 } d ? " - " + d : string.Empty)}");
        }

        return sb.Length == 0 ? ToolResult.Fail(program is null ? "The project has no routines." : $"No program '{program}'.") : ToolResult.Ok(sb.ToString());
    }

    private ToolResult ReadRoutine(JsonElement input) => ReadRoutineIn(input, FindRoutine(Required(input, "program"), Required(input, "routine")));

    private ToolResult ReadOtherRoutine(JsonElement input)
    {
        PlcProject other = Other().Project;
        string program = Required(input, "program");
        string name = Required(input, "routine");
        RoutineInfo routine = other.AllRoutines.FirstOrDefault(r => Same(r.Owner, program) && Same(r.Name, name))
            ?? throw new ArgumentException($"The other export has no routine {program}/{name}. compare_summary lists what differs.");
        return ReadRoutineIn(input, routine);
    }

    private static ToolResult ReadRoutineIn(JsonElement input, RoutineInfo routine)
    {
        int from = OptionalInt(input, "from_rung") ?? 0;
        int count = Math.Clamp(OptionalInt(input, "count") ?? 200, 1, 500);
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{routine.QualifiedName} [{LanguageText(routine)}]{(routine.Description is { Length: > 0 } d ? " - " + d : string.Empty)}");

        if (routine.IsProtected)
        {
            return ToolResult.Ok(sb.AppendLine("Source-protected: the export does not carry its logic.").ToString());
        }

        if (routine.Language == RoutineLanguage.StructuredText)
        {
            List<string> lines = routine.StructuredText.Skip(from).Take(count).ToList();
            for (int i = 0; i < lines.Count; i++)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{from + i,4}: {lines[i]}");
            }

            return ToolResult.Ok(sb.ToString());
        }

        if (routine.Language != RoutineLanguage.Ladder)
        {
            return ToolResult.Ok(sb.AppendLine(CultureInfo.InvariantCulture, $"{routine.Language} is graphical; LogicControl does not read its contents yet ({routine.GraphicalElementCount} sheets or steps).").ToString());
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"{routine.Rungs.Count} rungs; showing {from} to {Math.Min(routine.Rungs.Count, from + count) - 1}. Neutral text, as Studio 5000 stores it.");
        foreach (RungInfo rung in routine.Rungs.Skip(from).Take(count))
        {
            if (rung.Comment is { Length: > 0 } c)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  // {c.Replace("\n", " ", StringComparison.Ordinal).Replace("\r", string.Empty, StringComparison.Ordinal)}");
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"{rung.Number,4}: {rung.Text}");
        }

        return ToolResult.Ok(sb.ToString());
    }

    private ToolResult FindTags(JsonElement input)
    {
        ProjectAnalysis a = Analysis();
        string query = Optional(input, "query") ?? string.Empty;
        string? program = Optional(input, "program");
        string? type = Optional(input, "data_type");
        int limit = Math.Clamp(OptionalInt(input, "limit") ?? 60, 1, 300);

        List<TagInfo> matches = a.Project.AllTags
            .Where(t => program is null || Same(t.Scope, program) || (Same(program, "controller") && t.Scope is null))
            .Where(t => type is null || Same(t.DataType, type))
            .Where(t => query.Length == 0
                || t.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (t.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || (t.AliasFor?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

        if (matches.Count == 0)
        {
            return ToolResult.Ok("No tags match.");
        }

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{matches.Count} tag(s){(matches.Count > limit ? $", first {limit}" : string.Empty)}. scope | name | type | reads/writes | description");
        foreach (TagInfo t in matches.Take(limit))
        {
            IReadOnlyList<TagUse> uses = a.CrossReference.UsesOf(t);
            string kind = t.Kind switch
            {
                TagKind.Alias => $"alias for {t.AliasFor}",
                TagKind.Produced => $"{t.DataType} (produced)",
                TagKind.Consumed => $"{t.DataType} (consumed from {t.Consume?.Producer}.{t.Consume?.RemoteTag})",
                _ => t.DataType + (t.Dimensions is { } dims ? $"[{dims.Replace(' ', ',')}]" : string.Empty),
            };

            sb.AppendLine(CultureInfo.InvariantCulture, $"{t.Scope ?? "Controller"} | {t.Name} | {kind} | {uses.Count(u => u.Access == TagAccess.Read)}/{uses.Count(u => u.Access == TagAccess.Write)} | {t.Description}");
        }

        return ToolResult.Ok(sb.ToString());
    }

    private ToolResult TagReferences(JsonElement input)
    {
        ProjectAnalysis a = Analysis();
        string operand = Required(input, "tag");
        string? program = Optional(input, "program");
        string? access = Optional(input, "access");
        bool withText = OptionalBool(input, "include_rung_text") ?? true;

        string baseName = TagReference.BaseNames(operand).FirstOrDefault() ?? operand;
        List<TagInfo> tags = a.Project.AllTags
            .Where(t => Same(t.Name, baseName) && (program is null || Same(t.Scope, program) || t.Scope is null))
            .ToList();

        if (tags.Count == 0)
        {
            return ToolResult.Fail($"No tag '{baseName}' is declared{(program is null ? string.Empty : $" in {program} or at controller scope")}. find_tags searches by part of a name.");
        }

        bool member = operand.Length > baseName.Length;
        var sb = new StringBuilder();
        foreach (TagInfo tag in tags)
        {
            List<TagUse> uses = a.CrossReference.UsesOf(tag)
                .Where(u => !member || u.Operand.StartsWith(operand, StringComparison.OrdinalIgnoreCase))
                .Where(u => access is null || string.Equals(u.Access.ToString(), access, StringComparison.OrdinalIgnoreCase))
                .ToList();

            sb.AppendLine(CultureInfo.InvariantCulture, $"{tag.QualifiedName} ({tag.DataType ?? "alias for " + tag.AliasFor}) - {uses.Count} reference(s){(member ? $" to {operand}" : string.Empty)}{(tag.Description is { Length: > 0 } d ? ". " + d : string.Empty)}");
            foreach (TagUse u in uses.Take(150))
            {
                string text = withText && !u.IsStructuredText ? " :: " + RungText(u.Routine, u.Location) : string.Empty;
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {u.Access,-5} {u.Routine} {u.LocationText} {u.Instruction}({u.Operand}){text}");
            }

            if (uses.Count > 150)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  ... and {uses.Count - 150} more.");
            }
        }

        return ToolResult.Ok(sb.ToString());
    }

    private ToolResult GetDataType(JsonElement input)
    {
        string name = Required(input, "name");
        var sb = new StringBuilder();

        if (host.Drafts.DataTypes.FirstOrDefault(d => Same(d.Name, name)) is { } draft)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"UDT {draft.Name} (DRAFT){(draft.Description is { Length: > 0 } d ? " - " + d : string.Empty)}");
            foreach (MemberDraft m in draft.Members)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {m.Name} : {m.DataType}{(m.Dimension > 0 ? $"[{m.Dimension}]" : string.Empty)}{(m.Description is { Length: > 0 } md ? " // " + md : string.Empty)}");
            }

            return ToolResult.Ok(sb.ToString());
        }

        PlcProject? p = host.Analysis?.Project;
        if (p?.DataTypes.FirstOrDefault(d => Same(d.Name, name)) is { } udt)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"UDT {udt.Name}{(udt.Description is { Length: > 0 } d ? " - " + d : string.Empty)}");
            foreach (DataTypeMember m in udt.Members)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {m.Name} : {m.DataType}{(m.Dimension > 0 ? $"[{m.Dimension}]" : string.Empty)}{(m.Description is { Length: > 0 } md ? " // " + md : string.Empty)}");
            }

            return ToolResult.Ok(sb.ToString());
        }

        AoiInfo? aoi = p?.AddOnInstructions.FirstOrDefault(x => Same(x.Name, name));
        if (aoi is not null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"AOI {aoi.Name} rev {aoi.Revision}{(aoi.Description is { Length: > 0 } d ? " - " + d : string.Empty)}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Ladder call: {aoi.Name}(Instance{string.Concat(aoi.CallParameters.Select(c => "," + c.Name))})");
            foreach (TagInfo prm in aoi.Parameters)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {prm.Usage,-6} {prm.Name} : {prm.DataType}{(prm.Required ? " (required)" : string.Empty)}{(prm.Description is { Length: > 0 } pd ? " // " + pd : string.Empty)}");
            }

            foreach (TagInfo local in aoi.LocalTags)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Local  {local.Name} : {local.DataType}");
            }

            foreach (RoutineInfo r in aoi.Routines)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Routine {r.Name}: {r.Rungs.Count} rungs - read_routine with program '{aoi.Name}'");
            }

            return ToolResult.Ok(sb.ToString());
        }

        if (LogixTypes.MembersOf(name) is { } builtIn)
        {
            return ToolResult.Ok($"{LogixTypes.Canonical(name)} is built in. Members: {string.Join(", ", builtIn)}");
        }

        if (LogixTypes.IsAtomic(name))
        {
            return ToolResult.Ok($"{LogixTypes.Canonical(name)} is an atomic type{(LogixTypes.BitsOf(name) is int b ? $"; bits .0 to .{b - 1} are addressable" : string.Empty)}.");
        }

        return ToolResult.Fail($"No data type or AOI called '{name}' in the project or the drafts.");
    }

    private ToolResult ListFindings(JsonElement input)
    {
        ProjectAnalysis a = Analysis();
        string? severity = Optional(input, "severity");
        string? subject = Optional(input, "subject");

        List<Finding> findings = a.Findings
            .Where(f => severity is null || string.Equals(f.Severity.ToString(), severity, StringComparison.OrdinalIgnoreCase))
            .Where(f => subject is null || f.Subject.Contains(subject, StringComparison.OrdinalIgnoreCase) || (f.Location?.Contains(subject, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

        if (findings.Count == 0)
        {
            return ToolResult.Ok("No findings match.");
        }

        var sb = new StringBuilder();
        foreach (Finding f in findings.Take(200))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{f.Severity} {f.Rule} [{f.Category}] {f.Subject}: {f.Message}{(f.Location is { Length: > 0 } l ? " @ " + l : string.Empty)}");
        }

        return ToolResult.Ok(sb.ToString());
    }

    private ToolResult ListCommunications(JsonElement input)
    {
        ProjectAnalysis a = Analysis();
        string? filter = Optional(input, "filter");
        List<CommLink> links = a.Communications
            .Where(c => filter is null
                || c.From.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || c.To.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || (c.Address?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
                || c.KindText.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (links.Count == 0)
        {
            return ToolResult.Ok("No communication links match.");
        }

        var sb = new StringBuilder();
        foreach (CommLink c in links.Take(200))
        {
            string rpi = c.RpiMs is double r ? $", RPI {r.ToString(CultureInfo.InvariantCulture)} ms" : string.Empty;
            sb.AppendLine(CultureInfo.InvariantCulture, $"{c.KindText}: {c.From} -> {c.To}{(c.Address is { Length: > 0 } ad ? $" ({ad})" : string.Empty)}{rpi}{(c.Detail is { Length: > 0 } d ? "; " + d : string.Empty)}{(c.Source is { Length: > 0 } s ? " [from " + s + "]" : string.Empty)}");
        }

        return ToolResult.Ok(sb.ToString());
    }

    private ToolResult ListHardware(JsonElement input)
    {
        ProjectAnalysis a = Analysis();
        string? filter = Optional(input, "filter");
        var sb = new StringBuilder();

        foreach (HardwareNode n in a.Hardware.SelectMany(h => h.SelfAndDescendants()))
        {
            ModuleInfo m = n.Module;
            string line = $"{new string(' ', n.Depth * 2)}{m.Name} - {m.CatalogNumber} rev {m.Revision} @ {n.AddressText}{(m.Inhibited ? " INHIBITED" : string.Empty)}{(m.Description is { Length: > 0 } d ? " - " + d : string.Empty)}";
            if (filter is null || line.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine(line);
            }
        }

        return sb.Length == 0 ? ToolResult.Ok("No modules match.") : ToolResult.Ok(sb.ToString());
    }

    private static ToolResult InstructionHelp(JsonElement input)
    {
        string mnemonic = Required(input, "mnemonic");
        if (InstructionSignatures.Find(mnemonic) is not { } shape)
        {
            return ToolResult.Ok($"{mnemonic.ToUpperInvariant()} is not in LogicControl's instruction table. If it is an AOI, get_data_type describes it.");
        }

        string tail = shape.VariableTail is null ? string.Empty : $", then any number of {shape.VariableTail} operands";
        return ToolResult.Ok($"{shape.Mnemonic} - {shape.Summary}. Group {shape.Group}; drawn as {shape.Symbol}. Operands: {string.Join(", ", shape.OperandNames)}{tail}. Example: {shape.Mnemonic}({string.Join(",", shape.OperandNames.Select(_ => "?"))})");
    }

    // ------------------------------------------------------------------ checking and drafting

    private ToolResult CheckRungs(JsonElement input)
    {
        string program = Required(input, "program");
        List<string> rungs = Array(input, "rungs").Select(e => e.GetString() ?? string.Empty).ToList();
        var context = new DraftContext(host.Drafts, host.Analysis?.Project);
        RungScope scope = RungScope.ForProgram(context, program);

        var sb = new StringBuilder();
        for (int i = 0; i < rungs.Count; i++)
        {
            IReadOnlyList<DraftIssue> issues = DraftChecker.CheckRung(rungs[i], scope);
            sb.AppendLine(CultureInfo.InvariantCulture, $"rung {i}: {(issues.Count == 0 ? "OK" : string.Join(" | ", issues.Select(Describe)))}");
        }

        return ToolResult.Ok(sb.ToString());
    }

    private ToolResult DraftDataType(JsonElement input)
    {
        var udt = new UdtDraft
        {
            Name = Required(input, "name"),
            Description = Optional(input, "description"),
            Members = Array(input, "members").Select(m => new MemberDraft(
                Required(m, "name"),
                LogixTypes.Canonical(Required(m, "data_type")),
                Optional(m, "description"),
                OptionalInt(m, "dimension") ?? 0)).ToList(),
        };

        host.Drafts.DataTypes.RemoveAll(d => Same(d.Name, udt.Name));
        host.Drafts.DataTypes.Add(udt);
        return Drafted($"Data type {udt.Name} drafted with {udt.Members.Count} members.", udt);
    }

    private ToolResult DraftTags(JsonElement input)
    {
        var added = new List<TagDraft>();
        foreach (JsonElement t in Array(input, "tags"))
        {
            string spec = Required(t, "data_type");
            if (!LogixTypes.TryParseTypeSpec(spec, out string type, out string? dims))
            {
                type = spec;
                dims = null;
            }

            var tag = new TagDraft(Required(t, "name"), LogixTypes.Canonical(type), Optional(t, "description"), Optional(t, "program"))
            {
                Dimensions = Optional(t, "dimensions") ?? dims,
                AliasFor = Optional(t, "alias_for"),
            };

            host.Drafts.Tags.RemoveAll(x => Same(x.QualifiedName, tag.QualifiedName));
            host.Drafts.Tags.Add(tag);
            added.Add(tag);
        }

        return Drafted($"{added.Count} tag(s) drafted: {string.Join(", ", added.Select(t => t.QualifiedName))}.", added.Cast<object>().ToArray());
    }

    private ToolResult DraftRoutine(JsonElement input)
    {
        string program = Required(input, "program");
        string name = Required(input, "routine");
        string mode = Optional(input, "mode") ?? "replace";
        List<RungDraft> rungs = Array(input, "rungs")
            .Select(r => new RungDraft(L5xWriter.NormaliseRung(Required(r, "text")), Optional(r, "comment")))
            .ToList();

        RoutineDraft? draft = host.Drafts.Routines.FirstOrDefault(r => Same(r.Program, program) && Same(r.Name, name));
        string verb;

        if (draft is null)
        {
            // Starting from the project's own routine when appending to it, so "add a rung to
            // MainRoutine" produces the whole routine to import over the original, not one rung.
            draft = new RoutineDraft { Name = name, Program = program, Description = Optional(input, "description") };
            if (mode == "append" && host.Analysis?.Project.AllRoutines.FirstOrDefault(r => Same(r.Owner, program) && Same(r.Name, name)) is { } existing)
            {
                draft.Description ??= existing.Description;
                draft.Rungs.AddRange(existing.Rungs.Select(r => new RungDraft(r.Text, r.Comment)));
            }

            host.Drafts.Routines.Add(draft);
            verb = "created";
        }
        else
        {
            verb = "updated";
            draft.Description = Optional(input, "description") ?? draft.Description;
        }

        if (mode == "append")
        {
            draft.Rungs.AddRange(rungs);
        }
        else if (mode == "insert")
        {
            int at = Math.Clamp(OptionalInt(input, "at_rung") ?? draft.Rungs.Count, 0, draft.Rungs.Count);
            draft.Rungs.InsertRange(at, rungs);
        }
        else
        {
            draft.Rungs.Clear();
            draft.Rungs.AddRange(rungs);
        }

        return Drafted($"Routine {draft.QualifiedName} {verb} ({mode}): now {draft.Rungs.Count} rungs.", draft);
    }

    private ToolResult DraftAoi(JsonElement input)
    {
        var aoi = new AoiDraft
        {
            Name = Required(input, "name"),
            Revision = Optional(input, "revision") ?? "1.0",
            Description = Optional(input, "description"),
            Parameters = Array(input, "parameters").Select(p => new AoiParameterDraft(
                Required(p, "name"),
                LogixTypes.Canonical(Required(p, "data_type")),
                Optional(p, "usage") ?? "Input",
                OptionalBool(p, "required") ?? false,
                Optional(p, "description"))).ToList(),
            LocalTags = Array(input, "local_tags").Select(l => new AoiLocalTagDraft(
                Required(l, "name"), LogixTypes.Canonical(Required(l, "data_type")), Optional(l, "description"))).ToList(),
            Logic = Array(input, "rungs").Select(r => new RungDraft(L5xWriter.NormaliseRung(Required(r, "text")), Optional(r, "comment"))).ToList(),
        };

        host.Drafts.AddOnInstructions.RemoveAll(a => Same(a.Name, aoi.Name));
        host.Drafts.AddOnInstructions.Add(aoi);
        return Drafted($"Add-On {aoi.Name} drafted: {aoi.Parameters.Count} parameters, {aoi.LocalTags.Count} local tags, {aoi.Logic.Count} rungs.", aoi);
    }

    private ToolResult ListDrafts()
    {
        DevelopmentSet set = host.Drafts;
        if (set.IsEmpty)
        {
            return ToolResult.Ok("The development set is empty.");
        }

        var sb = new StringBuilder();
        foreach (UdtDraft d in set.DataTypes)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Data type {d.Name}: {d.Members.Count} members");
        }

        foreach (AoiDraft a in set.AddOnInstructions)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Add-On {a.Name}: {a.Parameters.Count} parameters, {a.Logic.Count} rungs");
        }

        foreach (ProgramDraft p in set.Programs)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Program {p.Name}");
        }

        foreach (RoutineDraft r in set.Routines)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Routine {r.QualifiedName}: {r.Rungs.Count} rungs");
        }

        foreach (TagDraft t in set.Tags)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Tag {t.QualifiedName} : {t.AliasFor ?? t.DataType}");
        }

        IReadOnlyList<DraftIssue> issues = DraftChecker.Check(set, host.Analysis?.Project);
        sb.AppendLine(CultureInfo.InvariantCulture, $"Checks: {issues.Count(i => i.IsError)} errors, {issues.Count(i => !i.IsError)} warnings.");
        foreach (DraftIssue i in issues.Take(60))
        {
            sb.AppendLine("  " + Describe(i));
        }

        return ToolResult.Ok(sb.ToString());
    }

    private ToolResult RemoveDraft(JsonElement input)
    {
        string kind = Required(input, "kind");
        string name = Required(input, "name");
        DevelopmentSet set = host.Drafts;

        int removed = kind.ToLowerInvariant() switch
        {
            "data_type" => set.DataTypes.RemoveAll(d => Same(d.Name, name)),
            "aoi" => set.AddOnInstructions.RemoveAll(a => Same(a.Name, name)),
            "routine" => set.Routines.RemoveAll(r => Same(r.QualifiedName, name) || Same(r.Name, name)),
            "tag" => set.Tags.RemoveAll(t => Same(t.QualifiedName, name) || Same(t.Name, name)),
            "program" => set.Programs.RemoveAll(p => Same(p.Name, name)),
            _ => throw new ArgumentException("kind must be data_type, aoi, routine, tag or program."),
        };

        if (removed > 0)
        {
            host.DraftsChanged($"Removed {kind} draft {name}");
        }

        return removed == 0 ? ToolResult.Fail($"No {kind} draft named '{name}'.") : ToolResult.Ok($"Removed {removed} {kind} draft(s).", changedDrafts: true);
    }

    /// <summary>After a draft tool: tell the host, then report the checks for what was just written.</summary>
    private ToolResult Drafted(string summary, params object[] targets)
    {
        host.DraftsChanged(summary);
        IReadOnlyList<DraftIssue> issues = DraftChecker.Check(host.Drafts, host.Analysis?.Project)
            .Where(i => i.Target is not null && targets.Contains(i.Target))
            .ToList();

        var sb = new StringBuilder(summary);
        sb.AppendLine();
        if (issues.Count == 0)
        {
            sb.AppendLine("Checks: no problems.");
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Checks: {issues.Count(i => i.IsError)} errors, {issues.Count(i => !i.IsError)} warnings - fix the errors before telling the user it is done.");
            foreach (DraftIssue i in issues.Take(60))
            {
                sb.AppendLine("  " + Describe(i));
            }
        }

        return ToolResult.Ok(sb.ToString(), changedDrafts: true);
    }

    // ------------------------------------------------------------------ helpers

    // ------------------------------------------------------------------ comparison

    private ProjectAnalysis Other() =>
        host.Comparison ?? throw new InvalidOperationException(
            host.CanOpenProjects
                ? "No comparison is open. Call open_comparison with the path of the other L5X export."
                : "No comparison is open. Ask the user to pick the other export on the Compare tab (Compare with...).");

    private ToolResult OpenComparison(JsonElement input)
    {
        string? problem = host.OpenComparison(Required(input, "path"));
        return problem is null ? CompareSummary(default) : ToolResult.Fail(problem);
    }

    private ChangeSet Comparison(string scope) =>
        ProjectComparison.Compare(Analysis().Project, Other().Project, scope);

    private ToolResult CompareSummary(JsonElement input)
    {
        PlcProject open = Analysis().Project;
        PlcProject other = Other().Project;
        string scope = input.ValueKind == JsonValueKind.Object ? Optional(input, "scope") ?? ProjectComparison.All : ProjectComparison.All;
        ChangeSet changes = Comparison(scope);

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Open project: {open.Controller.Name} ({Path.GetFileName(open.SourcePath)}). Other export: {other.Controller.Name} ({Path.GetFileName(other.SourcePath)}).");
        sb.AppendLine("Read as: open project -> other export. Added = only in the other export; Removed = only in the open project.");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{changes.Items.Count} differences{(scope.Length > 0 ? " in " + scope : string.Empty)}:");
        foreach (ItemChange c in changes.Items)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {c.What} {c.Name}: {ChangeSet.Verb(c.Kind)}{(c.Summary.Length > 0 ? " - " + c.Summary : string.Empty)}");
        }

        return ToolResult.Ok(sb.ToString());
    }

    private ToolResult CompareItem(JsonElement input)
    {
        string what = Required(input, "kind");
        string name = Required(input, "name");
        ChangeSet changes = Comparison(ProjectComparison.All);
        ItemChange? change = changes.Items.FirstOrDefault(c => Same(c.What, what) && Same(c.Name, name))
            ?? changes.Items.FirstOrDefault(c => Same(c.Name, name));
        return change is null
            ? ToolResult.Fail($"No difference for {what} {name}: the item is the same in both, or is not in either. compare_summary lists the differences.")
            : ToolResult.Ok(ProjectComparison.Describe(change));
    }

    private ProjectAnalysis Analysis() =>
        host.Analysis ?? throw new InvalidOperationException(
            host.CanOpenProjects
                ? "No project is open. Call open_project with the path of an L5X export."
                : "No project is open. Ask the user to open an L5X export (File > Open) - drafting still works without one.");

    private RoutineInfo FindRoutine(string program, string name) =>
        Analysis().Project.AllRoutines.FirstOrDefault(r => Same(r.Owner, program) && Same(r.Name, name))
        ?? throw new ArgumentException($"No routine {program}/{name}. list_routines shows what there is.");

    private string RungText(string qualifiedRoutine, int rung)
    {
        int slash = qualifiedRoutine.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0 || host.Analysis is not { } a)
        {
            return string.Empty;
        }

        RoutineInfo? r = a.Project.AllRoutines.FirstOrDefault(x => Same(x.QualifiedName, qualifiedRoutine));
        return r?.Rungs.FirstOrDefault(x => x.Number == rung)?.Text ?? string.Empty;
    }

    private static string RoutineLabel(RoutineInfo r) => r.Language == RoutineLanguage.Ladder ? $"{r.Name}({r.Rungs.Count})" : $"{r.Name}[{LanguageText(r)}]";

    private static string LanguageText(RoutineInfo r) => r.Language switch
    {
        RoutineLanguage.Ladder => "ladder",
        RoutineLanguage.StructuredText => "ST",
        RoutineLanguage.FunctionBlock => "FBD",
        RoutineLanguage.Sfc => "SFC",
        _ => "unknown",
    } + (r.IsProtected ? ", protected" : string.Empty);

    private static string Describe(DraftIssue i) => $"{(i.IsError ? "ERROR" : "warning")} {i.Location}: {i.Message}";

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Required(JsonElement e, string name) =>
        Optional(e, name) ?? throw new ArgumentException($"'{name}' is required.");

    private static string? Optional(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind != JsonValueKind.Null
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()) is { Length: > 0 } s ? s : null
            : null;

    private static int? OptionalInt(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v)
            ? v.ValueKind == JsonValueKind.Number ? v.GetInt32()
            : v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n
            : null
            : null;

    private static bool? OptionalBool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v)
            ? v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(v.GetString(), out bool b) ? b : null,
                _ => null,
            }
            : null;

    private static IEnumerable<JsonElement> Array(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out JsonElement v) || v.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        // Models now and then send an array as a JSON string; accept that rather than fail the turn.
        if (v.ValueKind == JsonValueKind.String)
        {
            v = JsonDocument.Parse(v.GetString() ?? "[]").RootElement;
        }

        return v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToList() : throw new ArgumentException($"'{name}' must be an array.");
    }

    // ------------------------------------------------------------------ the table

    /// <summary>The tool table as a host that can (or cannot) open projects would offer it.</summary>
    public static IReadOnlyList<ToolDefinition> DefinitionsFor(bool canOpenProjects) => BuildDefinitions(canOpenProjects);

    private static List<ToolDefinition> BuildDefinitions(bool canOpen)
    {
        var tools = new List<ToolDefinition>();

        if (canOpen)
        {
            tools.Add(new("open_project", "Open a Studio 5000 L5X export (a whole controller, program, routine or AOI export) and analyse it. Returns the project overview.",
                Schema(("path", "string", "Full path of the .L5X file", true)), Writes: false));
        }

        tools.AddRange(
        [
            new("project_overview", "The open project at a glance: controller, tasks and the programs they schedule, routines per program with rung counts, UDTs, AOIs, counts of tags, links and findings. Call this first.",
                Schema(), Writes: false),
            new("list_routines", "List routines with their language, optionally for one program (an AOI's name works as a program).",
                Schema(("program", "string", "Program name; omit for all", false)), Writes: false),
            new("read_routine", "Read a routine's rungs as neutral text with numbers and comments (or ST lines). Read before explaining or changing logic.",
                Schema(
                    ("program", "string", "Program name, or AOI name for AOI logic", true),
                    ("routine", "string", "Routine name", true),
                    ("from_rung", "integer", "First rung to return, default 0", false),
                    ("count", "integer", "How many rungs, default 200, max 500", false)), Writes: false),
            new("find_tags", "Search tags by part of the name, description or alias target, optionally by scope or data type. Shows scope, type, read/write counts and description.",
                Schema(
                    ("query", "string", "Text to look for; empty lists all", false),
                    ("program", "string", "A program name, or 'controller' for controller scope", false),
                    ("data_type", "string", "Only tags of this type, e.g. TIMER or a UDT name", false),
                    ("limit", "integer", "Default 60, max 300", false)), Writes: false),
            new("tag_references", "Every rung and ST line that reads or writes a tag (or a member such as Motor1.Run), with the rung text. Use it to trace why an output is on or off: find the writers, then what conditions they depend on.",
                Schema(
                    ("tag", "string", "Tag or member, e.g. M101 or M101.Fault", true),
                    ("program", "string", "Program to resolve program-scoped tags in", false),
                    ("access", "string", "Read or Write to filter", false),
                    ("include_rung_text", "boolean", "Default true", false)), Writes: false),
            new("get_data_type", "Describe a UDT (project or draft), an AOI (parameters, call form, local tags, routines) or a built-in type's members.",
                Schema(("name", "string", "Type or AOI name", true)), Writes: false),
            new("list_findings", "The analyser's findings: duplicate IPs, inhibited modules, double coils, uncalled routines, unhandled MSGs and more.",
                Schema(
                    ("severity", "string", "Error, Warning or Info", false),
                    ("subject", "string", "Text in the subject or location", false)), Writes: false),
            new("list_communications", "How the controller talks to everything: owned I/O connections, produced/consumed tags, MSG instructions with their paths and the rungs that fire them, GSV status reads.",
                Schema(("filter", "string", "Text in a module, tag, address or kind", false)), Writes: false),
            new("list_hardware", "The I/O tree: modules with catalog numbers, revisions, slots and IP addresses.",
                Schema(("filter", "string", "Text in a module line", false)), Writes: false),
            new("instruction_help", "Operand names and order for a built-in ladder instruction, e.g. TON, MOV, CPT, MSG, SCP.",
                Schema(("mnemonic", "string", "Instruction mnemonic", true)), Writes: false),
            new("check_rungs", "Check rung text without saving anything: syntax, operand counts, declared tags and members, as seen from a program. Use before drafting.",
                Schema(
                    ("program", "string", "Program the rungs would live in", true),
                    ("rungs", "array:string", "Rung neutral text, one per item", true)), Writes: false),
            new("draft_data_type", "Create or replace a UDT draft in the Develop tab. BOOLs are packed automatically. Returns the checker's verdict.",
                Schema(
                    ("name", "string", "UDT name", true),
                    ("description", "string", "Description", false),
                    ("members", "array:object", "Members: {name, data_type, dimension?, description?}", true)), Writes: true),
            new("draft_tags", "Create or replace tag drafts. Omit program for controller scope. data_type may carry dimensions, e.g. REAL[10].",
                Schema(("tags", "array:object", "Tags: {name, data_type, program?, description?, dimensions?, alias_for?}", true)), Writes: true),
            new("draft_routine", "Write a ladder routine draft in the Develop tab. mode 'replace' (default) sets all rungs; 'append' adds to the end - starting from the project's existing routine of that name, so the draft is the whole routine to import over the original; 'insert' puts rungs at at_rung. Rung text is Studio 5000 neutral text ending in ';'. Returns the checker's verdict.",
                Schema(
                    ("program", "string", "Program the routine belongs to", true),
                    ("routine", "string", "Routine name", true),
                    ("description", "string", "Routine description", false),
                    ("mode", "string", "replace, append or insert", false),
                    ("at_rung", "integer", "For insert: index to insert at", false),
                    ("rungs", "array:object", "Rungs: {text, comment?}", true)), Writes: true),
            new("draft_aoi", "Create or replace an Add-On Instruction draft: parameters (EnableIn/EnableOut are added automatically; Input/Output must be atomic types, structures are InOut), local tags and Logic rungs that may only use those.",
                Schema(
                    ("name", "string", "AOI name", true),
                    ("revision", "string", "e.g. 1.0", false),
                    ("description", "string", "Description", false),
                    ("parameters", "array:object", "{name, data_type, usage: Input|Output|InOut, required?, description?}", true),
                    ("local_tags", "array:object", "{name, data_type, description?}", false),
                    ("rungs", "array:object", "Logic rungs: {text, comment?}", true)), Writes: true),
            new("list_drafts", "Everything in the Develop tab now, with the current check results.",
                Schema(), Writes: false),
            new("remove_draft", "Remove a draft from the Develop tab.",
                Schema(
                    ("kind", "string", "data_type, aoi, routine, tag or program", true),
                    ("name", "string", "Name; a routine as Program/Routine", true)), Writes: true),
            new("compare_summary", "When the user is comparing the open project with another export: every difference, item by item (data types, Add-Ons, modules, tasks, programs, routines, tags), read as open project -> other export. Added means only in the other export.",
                Schema(("scope", "string", "A program name, '(controller)' for controller scope, or omit for everything", false)), Writes: false),
            new("compare_item", "The full difference for one item of the comparison: rungs added, removed and changed (both versions), fields changed.",
                Schema(
                    ("kind", "string", "Data type, Add-On, Module, Task, Program, Routine or Tag", true),
                    ("name", "string", "Name as compare_summary lists it; a routine as Program/Routine", true)), Writes: false),
            new("read_other_routine", "Read a routine from the other export of the comparison, like read_routine reads the open project's.",
                Schema(
                    ("program", "string", "Program name, or AOI name", true),
                    ("routine", "string", "Routine name", true),
                    ("from_rung", "integer", "First rung to return, default 0", false),
                    ("count", "integer", "How many rungs, default 200, max 500", false)), Writes: false),
        ]);

        if (canOpen)
        {
            tools.Add(new("open_comparison", "Open a second L5X export to compare the open project with. Returns the comparison summary.",
                Schema(("path", "string", "Full path of the other .L5X file", true)), Writes: false));
        }

        return tools;
    }

    private static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] properties)
    {
        var props = new JsonObject();
        foreach ((string name, string type, string description, bool _) in properties)
        {
            JsonObject p = type switch
            {
                "array:string" => new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                "array:object" => new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "object" } },
                _ => new JsonObject { ["type"] = type },
            };

            p["description"] = description;
            props[name] = p;
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = props };
        string[] required = properties.Where(p => p.Required).Select(p => p.Name).ToArray();
        if (required.Length > 0)
        {
            schema["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray());
        }

        return schema;
    }
}

/// <summary>What the tools work on. The app and the MCP server each provide one.</summary>
public interface IToolHost
{
    /// <summary>The open project, or null.</summary>
    ProjectAnalysis? Analysis { get; }

    /// <summary>The development set the draft tools write into. The same object the Develop tab edits.</summary>
    DevelopmentSet Drafts { get; }

    /// <summary>Whether open_project is offered. The app opens files itself; the MCP server lets Claude do it.</summary>
    bool CanOpenProjects { get; }

    /// <summary>Opens a project; returns why not, or null.</summary>
    string? OpenProject(string path);

    /// <summary>The other export the open project is being compared with, or null.</summary>
    ProjectAnalysis? Comparison => null;

    /// <summary>Opens the other export to compare with; returns why not, or null.</summary>
    string? OpenComparison(string path) => "Comparisons are opened from the Compare tab.";

    /// <summary>
    /// Called after a tool changed the drafts, so the Develop tab can refresh and save.
    /// <paramref name="summary"/> says what the tool did - it becomes the revision's label.
    /// </summary>
    void DraftsChanged(string summary);
}

/// <summary>A tool: name, what it is for, its input schema, and whether it changes the drafts.</summary>
public sealed record ToolDefinition(string Name, string Description, JsonObject InputSchema, bool Writes);

/// <summary>A tool's answer. <see cref="IsError"/> goes back to the model as is_error so it corrects itself.</summary>
public sealed record ToolResult(string Text, bool IsError, bool ChangedDrafts)
{
    public static ToolResult Ok(string text, bool changedDrafts = false) => new(text, false, changedDrafts);

    public static ToolResult Fail(string text) => new(text, true, false);
}
