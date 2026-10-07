using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using LogicControl.Core.Logic;

namespace LogicControl.Core.Authoring;

/// <summary>
/// Writes drafts as L5X - the same XML Studio 5000 exports, so it can import them back.
///
/// <para>Two levels. The element builders (<see cref="DataType"/>, <see cref="Tag"/>,
/// <see cref="Routine"/>, <see cref="Program"/>, <see cref="Aoi"/>) make the bare elements a
/// whole-project L5X holds; <see cref="ProjectMerger"/> drops them into an opened file. The
/// export methods wrap one of them as the <c>Use="Target"</c> of an import file and put what it
/// needs beside it as <c>Use="Context"</c> - the UDTs its tags are made of, the AOIs its rungs call,
/// the controller tags they use - so Studio 5000's import dialog can create those too.</para>
///
/// <para>What is written is what Studio 5000 writes, minus tag values: a tag is declared without a
/// <c>&lt;Data&gt;</c> block and starts at zero. BOOL members of a UDT are packed into hidden SINT
/// hosts with <c>BIT</c> members pointing into them, as Logix stores them; the reader hides the
/// hosts again, which is what the round-trip tests rely on.</para>
/// </summary>
public static class L5xWriter
{
    /// <summary>The options string Studio 5000 writes on a component export with context.</summary>
    private const string ExportOptions = "References NoRawData L5KData DecoratedData Context Dependencies ForceProtectedEncoding AllProjDocTrans";

    private const string HostPrefix = "ZZZZZZZZZZ";

    // ------------------------------------------------------------------ element builders

    /// <summary>A <c>&lt;DataType&gt;</c> for a UDT, with its BOOLs packed into hidden hosts.</summary>
    public static XElement DataType(UdtDraft udt)
    {
        ArgumentNullException.ThrowIfNull(udt);

        var members = new XElement("Members");
        XElement? host = null;
        int bit = 8;
        int hosts = 0;
        string stem = udt.Name.Length > LogixTypes.MaxNameLength - HostPrefix.Length - 2
            ? udt.Name[..(LogixTypes.MaxNameLength - HostPrefix.Length - 2)]
            : udt.Name;

        foreach (MemberDraft m in udt.Members)
        {
            bool packedBool = LogixTypes.IsBool(m.DataType) && m.Dimension == 0;
            if (packedBool)
            {
                if (host is null || bit == 8)
                {
                    host = new XElement("Member",
                        new XAttribute("Name", $"{HostPrefix}{stem}{hosts++}"),
                        new XAttribute("DataType", "SINT"),
                        new XAttribute("Dimension", "0"),
                        new XAttribute("Radix", "Decimal"),
                        new XAttribute("Hidden", "true"),
                        new XAttribute("ExternalAccess", "Read/Write"));
                    members.Add(host);
                    bit = 0;
                }

                members.Add(new XElement("Member",
                    new XAttribute("Name", m.Name),
                    new XAttribute("DataType", "BIT"),
                    new XAttribute("Dimension", "0"),
                    new XAttribute("Radix", "Decimal"),
                    new XAttribute("Hidden", "false"),
                    new XAttribute("Target", (string)host.Attribute("Name")!),
                    new XAttribute("BitNumber", bit++.ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("ExternalAccess", m.ExternalAccess),
                    DescriptionOf(m.Description)));
                continue;
            }

            // Anything that is not a lone BOOL ends the current host; the next BOOL starts a new one.
            host = null;

            string type = LogixTypes.Canonical(m.DataType);
            string radix = LogixTypes.IsBool(type) ? "Binary" : LogixTypes.RadixOf(type) ?? "NullType";

            members.Add(new XElement("Member",
                new XAttribute("Name", m.Name),
                new XAttribute("DataType", type),
                new XAttribute("Dimension", m.Dimension.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("Radix", radix),
                new XAttribute("Hidden", "false"),
                new XAttribute("ExternalAccess", m.ExternalAccess),
                DescriptionOf(m.Description)));
        }

        return new XElement("DataType",
            new XAttribute("Name", udt.Name),
            new XAttribute("Family", "NoFamily"),
            new XAttribute("Class", "User"),
            DescriptionOf(udt.Description),
            members);
    }

    /// <summary>A <c>&lt;Tag&gt;</c> - base or alias - with no data, so it starts at zero.</summary>
    public static XElement Tag(TagDraft tag)
    {
        ArgumentNullException.ThrowIfNull(tag);

        if (tag.AliasFor is { } target)
        {
            return new XElement("Tag",
                new XAttribute("Name", tag.Name),
                new XAttribute("TagType", "Alias"),
                new XAttribute("AliasFor", target),
                new XAttribute("ExternalAccess", tag.ExternalAccess),
                DescriptionOf(tag.Description));
        }

        string type = LogixTypes.Canonical(tag.DataType);
        var e = new XElement("Tag",
            new XAttribute("Name", tag.Name),
            new XAttribute("TagType", "Base"),
            new XAttribute("DataType", type));

        if (tag.Dimensions is { Length: > 0 } dims)
        {
            e.Add(new XAttribute("Dimensions", dims));
        }

        if (LogixTypes.RadixOf(type) is { } radix)
        {
            e.Add(new XAttribute("Radix", radix));
        }

        e.Add(
            new XAttribute("Constant", tag.Constant ? "true" : "false"),
            new XAttribute("ExternalAccess", tag.ExternalAccess),
            DescriptionOf(tag.Description));
        return e;
    }

    /// <summary>
    /// A Generic Ethernet <c>&lt;Module&gt;</c>, shaped like Studio 5000's own export of one: keying
    /// disabled (a generic module has no identity to check), one Ethernet port upstream, and one
    /// connection. No tag data is written - Studio 5000 creates the module's I/O tags, zeroed.
    /// Sizes go into the file in bytes; see <see cref="ModuleFormats"/> for what is unverified.
    /// </summary>
    public static XElement Module(ModuleDraft module)
    {
        ArgumentNullException.ThrowIfNull(module);
        string format = ModuleFormats.IsKnown(module.Format) ? module.Format : ModuleFormats.Dint;
        int rpi = (int)Math.Round(module.RpiMs * 1000.0);

        var connection = new XElement("Connection",
            new XAttribute("Name", "StandardConnection"),
            new XAttribute("RPI", rpi.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("Type", module.OutputSize > 0 ? "InputOutput" : "Input"),
            new XAttribute("EventID", "0"),
            new XAttribute("ProgrammaticallySendEventTrigger", "false"),
            new XAttribute("InputCxnPoint", module.InputInstance.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("OutputCxnPoint", module.OutputInstance.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("InputSize", ModuleFormats.Bytes(module.InputSize, format).ToString(CultureInfo.InvariantCulture)),
            new XAttribute("OutputSize", ModuleFormats.Bytes(module.OutputSize, format).ToString(CultureInfo.InvariantCulture)),
            new XAttribute("Unicast", module.Unicast ? "true" : "false"));

        return new XElement("Module",
            new XAttribute("Name", module.Name),
            new XAttribute("CatalogNumber", "ETHERNET-MODULE"),
            new XAttribute("Vendor", "0"),
            new XAttribute("ProductType", "0"),
            new XAttribute("ProductCode", "0"),
            new XAttribute("Major", "1"),
            new XAttribute("Minor", "1"),
            new XAttribute("ParentModule", module.ParentModule),
            new XAttribute("ParentModPortId", module.ParentPortId.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("Inhibited", "false"),
            new XAttribute("MajorFault", "false"),
            DescriptionOf(module.Description),
            new XElement("EKey", new XAttribute("State", "Disabled")),
            new XElement("Ports",
                new XElement("Port",
                    new XAttribute("Id", "2"),
                    new XAttribute("Address", module.IpAddress),
                    new XAttribute("Type", "Ethernet"),
                    new XAttribute("Upstream", "true"))),
            new XElement("Communications",
                new XAttribute("CommMethod", ModuleFormats.CommMethod(format).ToString(CultureInfo.InvariantCulture)),
                new XAttribute("ConfigCxnPoint", module.ConfigInstance.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("ConfigSize", module.ConfigSize.ToString(CultureInfo.InvariantCulture)),
                new XElement("Connections", connection)));
    }

    /// <summary>A ladder <c>&lt;Routine&gt;</c>.</summary>
    public static XElement Routine(string name, string? description, IEnumerable<RungDraft> rungs) =>
        new("Routine",
            new XAttribute("Name", name),
            new XAttribute("Type", "RLL"),
            DescriptionOf(description),
            new XElement("RLLContent", rungs.Select((r, i) => Rung(r, i))));

    public static XElement Routine(RoutineDraft routine)
    {
        ArgumentNullException.ThrowIfNull(routine);
        return Routine(routine.Name, routine.Description, routine.Rungs);
    }

    /// <summary>One <c>&lt;Rung&gt;</c>, numbered, with its text normalised to end in a semicolon.</summary>
    public static XElement Rung(RungDraft rung, int number)
    {
        ArgumentNullException.ThrowIfNull(rung);

        var e = new XElement("Rung",
            new XAttribute("Number", number.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("Type", "N"));

        if (!string.IsNullOrWhiteSpace(rung.Comment))
        {
            e.Add(new XElement("Comment", new XCData(rung.Comment.Trim())));
        }

        e.Add(new XElement("Text", new XCData(NormaliseRung(rung.Text))));
        return e;
    }

    /// <summary>A <c>&lt;Program&gt;</c> holding the given tags and routines.</summary>
    public static XElement Program(ProgramDraft program, IEnumerable<TagDraft> tags, IEnumerable<RoutineDraft> routines)
    {
        ArgumentNullException.ThrowIfNull(program);

        List<RoutineDraft> list = routines.ToList();
        string? main = program.MainRoutineName is { Length: > 0 } m ? m : list.FirstOrDefault()?.Name;

        var e = new XElement("Program",
            new XAttribute("Name", program.Name),
            new XAttribute("TestEdits", "false"));

        if (main is not null)
        {
            e.Add(new XAttribute("MainRoutineName", main));
        }

        e.Add(
            new XAttribute("Disabled", "false"),
            new XAttribute("UseAsFolder", "false"),
            DescriptionOf(program.Description),
            new XElement("Tags", tags.Select(Tag)),
            new XElement("Routines", list.Select(Routine)));
        return e;
    }

    /// <summary>An <c>&lt;AddOnInstructionDefinition&gt;</c> with EnableIn and EnableOut and a Logic routine.</summary>
    public static XElement Aoi(AoiDraft aoi, string softwareRevision, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(aoi);

        string stamp = utcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        var parameters = new XElement("Parameters",
            SystemParameter("EnableIn", "Input", "Enable Input - System Defined Parameter"),
            SystemParameter("EnableOut", "Output", "Enable Output - System Defined Parameter"));

        foreach (AoiParameterDraft p in aoi.Parameters)
        {
            string type = LogixTypes.Canonical(p.DataType);
            bool required = p.IsRequired;
            var e = new XElement("Parameter",
                new XAttribute("Name", p.Name),
                new XAttribute("TagType", "Base"),
                new XAttribute("DataType", type),
                new XAttribute("Usage", p.Usage));

            if (p.Usage != "InOut" && LogixTypes.RadixOf(type) is { } radix)
            {
                e.Add(new XAttribute("Radix", radix));
            }

            e.Add(
                new XAttribute("Required", required ? "true" : "false"),
                new XAttribute("Visible", required || p.Visible ? "true" : "false"));

            if (p.Usage == "InOut")
            {
                e.Add(new XAttribute("Constant", "false"));
            }
            else
            {
                e.Add(new XAttribute("ExternalAccess", p.Usage == "Output" ? "Read Only" : "Read/Write"));
            }

            e.Add(DescriptionOf(p.Description));
            parameters.Add(e);
        }

        var locals = new XElement("LocalTags");
        foreach (AoiLocalTagDraft l in aoi.LocalTags)
        {
            string type = LogixTypes.Canonical(l.DataType);
            var e = new XElement("LocalTag",
                new XAttribute("Name", l.Name),
                new XAttribute("DataType", type));

            if (l.Dimension > 0)
            {
                e.Add(new XAttribute("Dimensions", l.Dimension.ToString(CultureInfo.InvariantCulture)));
            }

            if (LogixTypes.RadixOf(type) is { } radix)
            {
                e.Add(new XAttribute("Radix", radix));
            }

            e.Add(new XAttribute("ExternalAccess", "None"), DescriptionOf(l.Description));
            locals.Add(e);
        }

        return new XElement("AddOnInstructionDefinition",
            new XAttribute("Name", aoi.Name),
            new XAttribute("Revision", aoi.Revision),
            new XAttribute("ExecutePrescan", "false"),
            new XAttribute("ExecutePostscan", "false"),
            new XAttribute("ExecuteEnableInFalse", "false"),
            new XAttribute("CreatedDate", stamp),
            new XAttribute("CreatedBy", "LogicControl"),
            new XAttribute("EditedDate", stamp),
            new XAttribute("EditedBy", "LogicControl"),
            new XAttribute("SoftwareRevision", "v" + softwareRevision),
            DescriptionOf(aoi.Description),
            parameters,
            locals,
            new XElement("Routines", Routine("Logic", null, aoi.Logic)));
    }

    // ------------------------------------------------------------------ import files

    /// <summary>A data type import file, with the UDTs it is built from as context.</summary>
    public static XDocument ExportDataType(DevelopmentSet set, UdtDraft udt, DateTime? now = null)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(udt);

        var deps = new Dependencies(set);
        deps.AddType(udt.Name);
        deps.Types.Remove(udt);

        XElement controller = Controller(set);
        controller.Add(new XElement("DataTypes", Use("Context"),
            deps.OrderedTypes().Select(t => Used(DataType(t), "Context")),
            Used(DataType(udt), "Target")));

        if (deps.Aois.Count > 0)
        {
            controller.Add(AoiContainer(set, deps, now));
        }

        return Document(set, udt.Name, "DataType", null, controller, now);
    }

    /// <summary>An Add-On Instruction import file, with its UDTs and nested AOIs as context.</summary>
    public static XDocument ExportAoi(DevelopmentSet set, AoiDraft aoi, DateTime? now = null)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(aoi);

        var deps = new Dependencies(set);
        deps.AddAoi(aoi.Name);
        deps.Aois.Remove(aoi);

        XElement controller = Controller(set);
        if (deps.Types.Count > 0)
        {
            controller.Add(new XElement("DataTypes", Use("Context"), deps.OrderedTypes().Select(t => Used(DataType(t), "Context"))));
        }

        controller.Add(new XElement("AddOnInstructionDefinitions", Use("Context"),
            deps.Aois.Select(a => Used(Aoi(a, set.SoftwareRevision, Now(now)), "Context")),
            Used(Aoi(aoi, set.SoftwareRevision, Now(now)), "Target")));

        return Document(set, aoi.Name, "AddOnInstructionDefinition", null, controller, now);
    }

    /// <summary>
    /// A routine import file. Imported into an existing program; the controller and program tags
    /// its rungs use, the UDTs those are made of and the AOIs it calls - the drafted ones - travel
    /// with it as context.
    /// </summary>
    public static XDocument ExportRoutine(DevelopmentSet set, RoutineDraft routine, DateTime? now = null)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(routine);

        var deps = new Dependencies(set);
        deps.AddRungs(routine.Rungs, routine.Program);

        XElement controller = Controller(set);
        AddContext(set, controller, deps, now);

        List<TagDraft> programTags = deps.Tags.Where(t => t.Program is not null).ToList();
        controller.Add(new XElement("Programs", Use("Context"),
            new XElement("Program", Use("Context"), new XAttribute("Name", routine.Program),
                new XElement("Tags", Use("Context"), programTags.Select(t => Used(Tag(t), "Context"))),
                new XElement("Routines", Use("Context"), Used(Routine(routine), "Target")))));

        return Document(set, routine.Name, "Routine", "RLL", controller, now);
    }

    /// <summary>A program import file: the program, its drafted tags and routines, and their context.</summary>
    public static XDocument ExportProgram(DevelopmentSet set, ProgramDraft program, DateTime? now = null)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(program);

        List<RoutineDraft> routines = set.Routines.Where(r => Same(r.Program, program.Name)).ToList();
        List<TagDraft> ownTags = set.Tags.Where(t => Same(t.Program, program.Name)).ToList();

        var deps = new Dependencies(set);
        foreach (RoutineDraft r in routines)
        {
            deps.AddRungs(r.Rungs, program.Name);
        }

        foreach (TagDraft t in ownTags)
        {
            deps.AddType(t.DataType);
        }

        XElement controller = Controller(set);
        AddContext(set, controller, deps, now);
        controller.Add(new XElement("Programs", Use("Context"),
            Used(Program(program, ownTags, routines), "Target")));

        return Document(set, program.Name, "Program", null, controller, now);
    }

    /// <summary>
    /// Every import file the set makes, named for what it holds and numbered in the order they
    /// should be imported: data types, then AOIs, then programs, then routines into existing programs.
    /// </summary>
    public static IReadOnlyList<(string FileName, XDocument Document)> ExportAll(DevelopmentSet set, DateTime? now = null)
    {
        ArgumentNullException.ThrowIfNull(set);
        var files = new List<(string, XDocument)>();
        int n = 1;

        var deps = new Dependencies(set);
        foreach (UdtDraft t in set.DataTypes)
        {
            deps.AddType(t.Name);
        }

        foreach (UdtDraft udt in deps.OrderedTypes())
        {
            files.Add(($"{n++:00}_DataType_{udt.Name}.L5X", ExportDataType(set, udt, now)));
        }

        foreach (AoiDraft aoi in set.AddOnInstructions)
        {
            files.Add(($"{n++:00}_AOI_{aoi.Name}.L5X", ExportAoi(set, aoi, now)));
        }

        var programs = new HashSet<string>(set.Programs.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        foreach (ProgramDraft program in set.Programs)
        {
            files.Add(($"{n++:00}_Program_{program.Name}.L5X", ExportProgram(set, program, now)));
        }

        foreach (RoutineDraft routine in set.Routines.Where(r => !programs.Contains(r.Program)))
        {
            files.Add(($"{n++:00}_Routine_{routine.Program}_{routine.Name}.L5X", ExportRoutine(set, routine, now)));
        }

        return files;
    }

    /// <summary>Serialises as Studio 5000 does: UTF-8, standalone, CDATA kept.</summary>
    public static string ToText(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = true,
            IndentChars = string.Empty,
            NewLineChars = "\r\n",
        };

        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings))
        {
            document.Save(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static void Save(XDocument document, string path) => File.WriteAllText(path, ToText(document), new UTF8Encoding(false));

    /// <summary>
    /// Tidies rung text the way Studio 5000 stores it: one line, no spaces outside operands and
    /// quotes, ending in exactly one semicolon. An empty rung stays empty-with-semicolon.
    /// </summary>
    public static string NormaliseRung(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ";";
        }

        var sb = new StringBuilder(text.Length);
        bool quoted = false;
        int depth = 0;
        foreach (char c in text)
        {
            if (c == '\'')
            {
                quoted = !quoted;
            }

            if (!quoted)
            {
                if (c == '(')
                {
                    depth++;
                }
                else if (c == ')')
                {
                    depth = Math.Max(0, depth - 1);
                }
                else if (c is '\r' or '\n' or '\t')
                {
                    if (depth > 0)
                    {
                        sb.Append(' ');
                    }

                    continue;
                }
                else if (c == ' ' && depth == 0)
                {
                    continue;
                }
            }

            sb.Append(c);
        }

        string s = sb.ToString().TrimEnd(';', ' ');

        // A rung that parses cleanly is rebuilt from its tree, which also trims the spaces people
        // type around operands - "XIC( A )" is stored as "XIC(A)". One that does not parse is left
        // as typed, so the checker's message still points at what was written.
        LadderRung rung = LadderParser.Parse(s);
        return (rung.IsValid ? Write(rung.Root) : s) + ";";
    }

    private static string Write(LadderSeries series) => string.Concat(series.Items.Select(n => n switch
    {
        LadderInstruction i => $"{i.Instruction.Mnemonic}({string.Join(",", i.Instruction.Operands)})",
        LadderBranch b => "[" + string.Join(",", b.Legs.Select(Write)) + "]",
        _ => string.Empty,
    }));

    // ------------------------------------------------------------------ internals

    private static void AddContext(DevelopmentSet set, XElement controller, Dependencies deps, DateTime? now)
    {
        if (deps.Types.Count > 0)
        {
            controller.Add(new XElement("DataTypes", Use("Context"), deps.OrderedTypes().Select(t => Used(DataType(t), "Context"))));
        }

        if (deps.Aois.Count > 0)
        {
            controller.Add(AoiContainer(set, deps, now));
        }

        List<TagDraft> controllerTags = deps.Tags.Where(t => t.Program is null).ToList();
        if (controllerTags.Count > 0)
        {
            controller.Add(new XElement("Tags", Use("Context"), controllerTags.Select(t => Used(Tag(t), "Context"))));
        }
    }

    private static XElement AoiContainer(DevelopmentSet set, Dependencies deps, DateTime? now) =>
        new("AddOnInstructionDefinitions", Use("Context"),
            deps.Aois.Select(a => Used(Aoi(a, set.SoftwareRevision, Now(now)), "Context")));

    private static XElement Controller(DevelopmentSet set) =>
        new("Controller", Use("Context"), new XAttribute("Name", set.ControllerName));

    private static XDocument Document(DevelopmentSet set, string targetName, string targetType, string? subType, XElement controller, DateTime? now)
    {
        var root = new XElement("RSLogix5000Content",
            new XAttribute("SchemaRevision", "1.0"),
            new XAttribute("SoftwareRevision", set.SoftwareRevision),
            new XAttribute("TargetName", targetName),
            new XAttribute("TargetType", targetType));

        if (subType is not null)
        {
            root.Add(new XAttribute("TargetSubType", subType));
        }

        root.Add(
            new XAttribute("ContainsContext", "true"),
            new XAttribute("ExportDate", Now(now).ToLocalTime().ToString("ddd MMM dd HH:mm:ss yyyy", CultureInfo.InvariantCulture)),
            new XAttribute("ExportOptions", ExportOptions),
            controller);

        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root);
    }

    private static DateTime Now(DateTime? now) => now ?? DateTime.UtcNow;

    private static XAttribute Use(string use) => new("Use", use);

    /// <summary>Puts Use first, where Studio 5000 writes it.</summary>
    private static XElement Used(XElement e, string use)
    {
        List<XAttribute> attributes = e.Attributes().ToList();
        e.RemoveAttributes();
        e.Add(new XAttribute("Use", use));
        e.Add(attributes);
        return e;
    }

    private static XElement? DescriptionOf(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : new XElement("Description", new XCData(text.Trim()));

    private static XElement SystemParameter(string name, string usage, string description) =>
        new("Parameter",
            new XAttribute("Name", name),
            new XAttribute("TagType", "Base"),
            new XAttribute("DataType", "BOOL"),
            new XAttribute("Usage", usage),
            new XAttribute("Radix", "Decimal"),
            new XAttribute("Required", "false"),
            new XAttribute("Visible", "false"),
            new XAttribute("ExternalAccess", "Read Only"),
            new XElement("Description", new XCData(description)));

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Collects the drafted UDTs, AOIs and tags something needs, transitively: a rung's tags, their
    /// types, those types' member types, the AOIs the rung calls and what those AOIs are built of.
    /// Only drafts - anything already in the project is in the project it is imported into.
    /// </summary>
    internal sealed class Dependencies(DevelopmentSet set)
    {
        public List<UdtDraft> Types { get; } = [];

        public List<AoiDraft> Aois { get; } = [];

        public List<TagDraft> Tags { get; } = [];

        public void AddType(string? name)
        {
            if (name is null)
            {
                return;
            }

            if (set.DataTypes.FirstOrDefault(t => Same(t.Name, name)) is { } udt)
            {
                if (Types.Contains(udt))
                {
                    return;
                }

                Types.Add(udt);
                foreach (MemberDraft m in udt.Members)
                {
                    AddType(m.DataType);
                }
            }
            else
            {
                AddAoi(name);
            }
        }

        public void AddAoi(string name)
        {
            if (set.AddOnInstructions.FirstOrDefault(a => Same(a.Name, name)) is not { } aoi || Aois.Contains(aoi))
            {
                return;
            }

            // Nested AOIs and types first, so the list is in import order.
            foreach (AoiParameterDraft p in aoi.Parameters)
            {
                AddType(p.DataType);
            }

            foreach (AoiLocalTagDraft l in aoi.LocalTags)
            {
                AddType(l.DataType);
            }

            foreach (Instruction i in aoi.Logic.SelectMany(r => RungParser.Parse(r.Text)))
            {
                AddAoi(i.Mnemonic);
            }

            Aois.Add(aoi);
        }

        public void AddRungs(IEnumerable<RungDraft> rungs, string program)
        {
            foreach (Instruction instruction in rungs.SelectMany(r => RungParser.Parse(r.Text)))
            {
                AddAoi(instruction.Mnemonic);

                for (int i = 0; i < instruction.Operands.Count; i++)
                {
                    if (InstructionCatalog.RoleOf(instruction.Mnemonic, i) == OperandRole.NotATag)
                    {
                        continue;
                    }

                    foreach (string name in TagReference.BaseNames(instruction.Operands[i]))
                    {
                        TagDraft? tag = set.Tags.FirstOrDefault(t => Same(t.Name, name) && Same(t.Program, program))
                            ?? set.Tags.FirstOrDefault(t => Same(t.Name, name) && t.Program is null);

                        if (tag is not null && !Tags.Contains(tag))
                        {
                            Tags.Add(tag);
                            AddType(tag.DataType);
                        }
                    }
                }
            }
        }

        /// <summary>The types with every type a member uses before it.</summary>
        public List<UdtDraft> OrderedTypes()
        {
            var ordered = new List<UdtDraft>();
            var visiting = new HashSet<UdtDraft>();

            void Visit(UdtDraft t)
            {
                if (ordered.Contains(t) || !visiting.Add(t))
                {
                    return;
                }

                foreach (MemberDraft m in t.Members)
                {
                    if (Types.FirstOrDefault(x => Same(x.Name, m.DataType)) is { } dep)
                    {
                        Visit(dep);
                    }
                }

                ordered.Add(t);
            }

            foreach (UdtDraft t in Types)
            {
                Visit(t);
            }

            return ordered;
        }
    }
}
