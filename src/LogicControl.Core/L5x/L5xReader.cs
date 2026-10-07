using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using LogicControl.Core.Model;

namespace LogicControl.Core.L5x;

/// <summary>
/// Reads a Studio 5000 L5X export into a <see cref="PlcProject"/>.
///
/// <para><b>Tolerant by design.</b> L5X has grown attributes across twenty years of Logix
/// versions, and an export from v20 and one from v36 are both normal things to be handed. Every
/// attribute is optional here: a missing one becomes null, an unparseable number becomes null,
/// and an element this reader does not know is skipped rather than failed on. The only hard
/// failure is a file that is not L5X at all.</para>
///
/// <para><b>Read-only, and it never resolves external entities.</b> DTD processing is prohibited
/// - an export never has one, and a file that does is not something to follow links out of.</para>
///
/// <para>The .ACD project file is a proprietary binary and is not read here. Studio 5000's
/// File &gt; Save As &gt; L5X produces the export, and the Logix Designer SDK can do the same in a
/// batch - see PLAN.md.</para>
/// </summary>
public static class L5xReader
{
    private const string RootName = "RSLogix5000Content";

    /// <summary>Reads the file at <paramref name="path"/>.</summary>
    /// <exception cref="L5xFormatException">It is not an L5X export.</exception>
    public static PlcProject Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (path.EndsWith(".acd", StringComparison.OrdinalIgnoreCase))
        {
            throw new L5xFormatException(
                "That is an .ACD project file, which is a proprietary binary format. "
                + "Open it in Studio 5000 and use File > Save As with the type set to L5X, "
                + "then open the .L5X file here.");
        }

        using FileStream stream = File.OpenRead(path);
        return Load(stream, path);
    }

    /// <summary>Reads an export from a stream; <paramref name="sourcePath"/> is only for display.</summary>
    /// <exception cref="L5xFormatException">It is not an L5X export.</exception>
    public static PlcProject Load(Stream stream, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(stream);

        XDocument document;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
            };

            using var reader = XmlReader.Create(stream, settings);
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            throw new L5xFormatException(
                $"The file is not well-formed XML (line {ex.LineNumber}, position {ex.LinePosition}): {ex.Message}",
                ex);
        }

        XElement root = document.Root
            ?? throw new L5xFormatException("The file is empty.");

        if (root.Name.LocalName != RootName)
        {
            throw new L5xFormatException(
                $"Expected an L5X export (a <{RootName}> document) but found <{root.Name.LocalName}>. "
                + "Export the project from Studio 5000 with File > Save As > L5X.");
        }

        XElement controller = root.Element("Controller")
            ?? throw new L5xFormatException("The export has no <Controller> element.");

        return new PlcProject
        {
            SourcePath = sourcePath,
            SoftwareRevision = Str(root, "SoftwareRevision"),
            TargetType = Str(root, "TargetType"),
            TargetName = Str(root, "TargetName"),
            ExportDate = Str(root, "ExportDate"),
            Controller = ReadController(controller),
            Modules = Children(controller, "Modules", "Module").Select(ReadModule).ToList(),
            DataTypes = Children(controller, "DataTypes", "DataType").Select(ReadDataType).ToList(),
            AddOnInstructions = ReadAois(controller.Element("AddOnInstructionDefinitions")),
            Tags = Children(controller, "Tags", "Tag").Select(t => ReadTag(t, scope: null)).ToList(),
            Programs = Children(controller, "Programs", "Program").Select(ReadProgram).ToList(),
            Tasks = Children(controller, "Tasks", "Task").Select(ReadTask).ToList(),
        };
    }

    // ------------------------------------------------------------------
    //  Controller and I/O tree
    // ------------------------------------------------------------------

    private static ControllerInfo ReadController(XElement e) => new()
    {
        Name = Str(e, "Name") ?? "(unnamed controller)",
        ProcessorType = Str(e, "ProcessorType"),
        MajorRevision = Int(e, "MajorRev"),
        MinorRevision = Int(e, "MinorRev"),
        Description = Description(e),
        IsSafety = e.Element("SafetyInfo") is not null,
    };

    private static ModuleInfo ReadModule(XElement e)
    {
        XElement? communications = e.Element("Communications");

        return new ModuleInfo
        {
            Name = Str(e, "Name") ?? "(unnamed module)",
            CatalogNumber = Str(e, "CatalogNumber"),
            Vendor = Int(e, "Vendor"),
            ProductType = Int(e, "ProductType"),
            ProductCode = Int(e, "ProductCode"),
            MajorRevision = Int(e, "Major"),
            MinorRevision = Int(e, "Minor"),
            ParentModule = Str(e, "ParentModule"),
            ParentPortId = Int(e, "ParentModPortId"),
            Inhibited = Bool(e, "Inhibited"),
            MajorFaultOnConnectionFailure = Bool(e, "MajorFault"),
            Keying = Str(e.Element("EKey"), "State"),
            Description = Description(e),
            Ports = Children(e, "Ports", "Port")
                .Select(p => new PortInfo(
                    Int(p, "Id") ?? 0,
                    Str(p, "Type"),
                    Str(p, "Address"),
                    Bool(p, "Upstream")))
                .ToList(),
            Connections = communications?
                .Element("Connections")?
                .Elements("Connection")
                .Select(ReadConnection)
                .ToList() ?? [],
            ConfigInstance = communications is null ? null
                : FirstInt(communications, "ConfigCxnPoint") ?? FirstInt(e, "ConfigCxnPoint"),
            ConfigSize = communications is null ? null : FirstInt(communications, "ConfigSize"),
        };
    }

    private static ConnectionInfo ReadConnection(XElement c) => new()
    {
        Name = Str(c, "Name"),
        RpiMicroseconds = Int(c, "RPI"),
        Type = Str(c, "Type"),
        InputInstance = Int(c, "InputCxnPoint"),
        OutputInstance = Int(c, "OutputCxnPoint"),
        InputSize = Int(c, "InputSize"),
        OutputSize = Int(c, "OutputSize"),
        Unicast = c.Attribute("Unicast") is null ? null : Bool(c, "Unicast"),
    };

    // ------------------------------------------------------------------
    //  Data types and AOIs
    // ------------------------------------------------------------------

    private static DataTypeInfo ReadDataType(XElement e) => new()
    {
        Name = Str(e, "Name") ?? "(unnamed type)",
        Family = Str(e, "Family"),
        Description = Description(e),
        Members = Children(e, "Members", "Member")
            .Where(m => !Bool(m, "Hidden"))
            .Select(m => new DataTypeMember(
                Str(m, "Name") ?? string.Empty,
                Str(m, "DataType") is "BIT" ? "BOOL" : Str(m, "DataType"),
                Int(m, "Dimension") ?? 0,
                Description(m)))
            .ToList(),
    };

    private static List<AoiInfo> ReadAois(XElement? container)
    {
        if (container is null)
        {
            return [];
        }

        var aois = new List<AoiInfo>();
        foreach (XElement e in container.Elements())
        {
            switch (e.Name.LocalName)
            {
                case "AddOnInstructionDefinition":
                    aois.Add(ReadAoi(e));
                    break;

                // A source-protected AOI is exported as an encrypted blob that keeps only its name.
                case "EncodedData" when Str(e, "Type") is null or "AddOnInstructionDefinition":
                    aois.Add(new AoiInfo { Name = Str(e, "Name") ?? "(protected)", IsProtected = true });
                    break;

                default:
                    break;
            }
        }

        return aois;
    }

    private static AoiInfo ReadAoi(XElement e)
    {
        string name = Str(e, "Name") ?? "(unnamed AOI)";
        List<TagInfo> parameters = Children(e, "Parameters", "Parameter")
            .Select(p => ReadTag(p, scope: name))
            .ToList();

        return new AoiInfo
        {
            Name = name,
            Revision = Str(e, "Revision"),
            Vendor = Str(e, "Vendor"),
            Description = Description(e),
            Parameters = parameters,
            LocalTags = Children(e, "LocalTags", "LocalTag").Select(t => ReadTag(t, scope: name)).ToList(),
            Routines = ReadRoutines(e, name, ownerIsAoi: true),
            CallParameters = parameters
                .Where(p => p.Required
                    && p.Name is not ("EnableIn" or "EnableOut"))
                .ToList(),
        };
    }

    // ------------------------------------------------------------------
    //  Tags
    // ------------------------------------------------------------------

    private static TagInfo ReadTag(XElement e, string? scope)
    {
        string? usage = Str(e, "Usage");
        XElement? consume = e.Element("ConsumeInfo");

        return new TagInfo
        {
            Name = Str(e, "Name") ?? "(unnamed tag)",
            Scope = scope,
            DataType = Str(e, "DataType"),
            Dimensions = NullIfZero(Str(e, "Dimensions")),
            Kind = Str(e, "TagType") switch
            {
                "Alias" => TagKind.Alias,
                "Produced" => TagKind.Produced,
                "Consumed" => TagKind.Consumed,
                _ => TagKind.Base,
            },
            AliasFor = Str(e, "AliasFor"),
            Description = Description(e),
            ExternalAccess = Str(e, "ExternalAccess"),
            Constant = Bool(e, "Constant"),
            Usage = usage,
            Required = Bool(e, "Required") || string.Equals(usage, "InOut", StringComparison.Ordinal),
            Consume = consume is null ? null : new ConsumeInfo(
                Str(consume, "Producer"),
                Str(consume, "RemoteTag"),
                Double(consume, "RPI"),
                consume.Attribute("Unicast") is null ? null : Bool(consume, "Unicast")),
            ProduceCount = Int(e.Element("ProduceInfo"), "ProduceCount"),
            Message = ReadMessage(e),
        };
    }

    private static MessageInfo? ReadMessage(XElement tag)
    {
        XElement? p = tag.Elements("Data")
            .Where(d => string.Equals(Str(d, "Format"), "Message", StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Element("MessageParameters"))
            .FirstOrDefault(m => m is not null);

        if (p is null)
        {
            return null;
        }

        return new MessageInfo
        {
            MessageType = Str(p, "MessageType"),
            ConnectionPath = Str(p, "ConnectionPath"),
            RemoteElement = Str(p, "RemoteElement"),
            LocalElement = Str(p, "LocalElement") ?? Str(p, "DestinationTag"),
            RequestedLength = Int(p, "RequestedLength"),
            Connected = Bool(p, "ConnectedFlag"),
            CacheConnections = Bool(p, "CacheConnections"),
            ServiceCode = Str(p, "ServiceCode"),
            ObjectClass = Str(p, "ObjectType"),
            Instance = Str(p, "TargetObject"),
            Attribute = Str(p, "AttributeNumber"),
        };
    }

    // ------------------------------------------------------------------
    //  Programs, routines, tasks
    // ------------------------------------------------------------------

    private static ProgramInfo ReadProgram(XElement e)
    {
        string name = Str(e, "Name") ?? "(unnamed program)";
        return new ProgramInfo
        {
            Name = name,
            MainRoutineName = Str(e, "MainRoutineName"),
            FaultRoutineName = Str(e, "FaultRoutineName"),
            Disabled = Bool(e, "Disabled"),
            Description = Description(e),
            Tags = Children(e, "Tags", "Tag").Select(t => ReadTag(t, scope: name)).ToList(),
            Routines = ReadRoutines(e, name, ownerIsAoi: false),
        };
    }

    private static List<RoutineInfo> ReadRoutines(XElement owner, string ownerName, bool ownerIsAoi)
    {
        XElement? container = owner.Element("Routines");
        if (container is null)
        {
            return [];
        }

        var routines = new List<RoutineInfo>();
        foreach (XElement e in container.Elements())
        {
            if (e.Name.LocalName == "Routine")
            {
                routines.Add(ReadRoutine(e, ownerName, ownerIsAoi));
            }
            else if (e.Name.LocalName == "EncodedData")
            {
                routines.Add(new RoutineInfo
                {
                    Name = Str(e, "Name") ?? "(protected)",
                    Owner = ownerName,
                    OwnerIsAoi = ownerIsAoi,
                    Language = LanguageOf(Str(e, "Type")),
                    IsProtected = true,
                });
            }
        }

        return routines;
    }

    private static RoutineInfo ReadRoutine(XElement e, string ownerName, bool ownerIsAoi)
    {
        RoutineLanguage language = LanguageOf(Str(e, "Type"));

        List<RungInfo> rungs = e.Element("RLLContent")?
            .Elements("Rung")
            .Select((r, i) => new RungInfo(
                Int(r, "Number") ?? i,
                (r.Element("Text")?.Value ?? string.Empty).Trim(),
                Text(r.Element("Comment")),
                Str(r, "Type")))
            .ToList() ?? [];

        List<string> st = e.Element("STContent")?
            .Elements("Line")
            .Select(l => l.Value)
            .ToList() ?? [];

        int graphical = language switch
        {
            RoutineLanguage.FunctionBlock => e.Element("FBDContent")?.Elements("Sheet").Count() ?? 0,
            RoutineLanguage.Sfc => e.Element("SFCContent")?.Elements("Step").Count() ?? 0,
            _ => 0,
        };

        return new RoutineInfo
        {
            Name = Str(e, "Name") ?? "(unnamed routine)",
            Owner = ownerName,
            OwnerIsAoi = ownerIsAoi,
            Language = language,
            Description = Description(e),
            Rungs = rungs,
            StructuredText = st,
            GraphicalElementCount = graphical,
            IsProtected = e.Element("EncodedData") is not null,
        };
    }

    private static RoutineLanguage LanguageOf(string? type) => type switch
    {
        "RLL" => RoutineLanguage.Ladder,
        "ST" => RoutineLanguage.StructuredText,
        "FBD" => RoutineLanguage.FunctionBlock,
        "SFC" => RoutineLanguage.Sfc,
        _ => RoutineLanguage.Unknown,
    };

    private static TaskInfo ReadTask(XElement e) => new()
    {
        Name = Str(e, "Name") ?? "(unnamed task)",
        Type = Str(e, "Type"),
        RateMs = Double(e, "Rate"),
        Priority = Int(e, "Priority"),
        WatchdogMs = Int(e, "Watchdog"),
        InhibitTask = Bool(e, "InhibitTask"),
        ScheduledPrograms = Children(e, "ScheduledPrograms", "ScheduledProgram")
            .Select(p => Str(p, "Name"))
            .OfType<string>()
            .ToList(),
    };

    // ------------------------------------------------------------------
    //  Attribute helpers - every one of them tolerates absence
    // ------------------------------------------------------------------

    private static IEnumerable<XElement> Children(XElement parent, string container, string item) =>
        parent.Element(container)?.Elements(item) ?? [];

    private static string? Str(XElement? e, string attribute)
    {
        string? value = e?.Attribute(attribute)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static int? Int(XElement? e, string attribute) => ParseInt(Str(e, attribute));

    /// <summary>
    /// Integers in L5X are usually decimal, but message parameters and a few others are written
    /// in Logix radix notation: 16#004c, 2#0101, 8#17.
    /// </summary>
    internal static int? ParseInt(string? raw)
    {
        if (raw is null)
        {
            return null;
        }

        string s = raw.Replace("_", string.Empty, StringComparison.Ordinal);
        int hash = s.IndexOf('#', StringComparison.Ordinal);
        if (hash > 0 && int.TryParse(s.AsSpan(0, hash), NumberStyles.None, CultureInfo.InvariantCulture, out int radix)
            && radix is 2 or 8 or 16)
        {
            try
            {
                return Convert.ToInt32(s[(hash + 1)..], radix);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
            {
                return null;
            }
        }

        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;
    }

    private static double? Double(XElement? e, string attribute) =>
        double.TryParse(Str(e, attribute), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : null;

    /// <summary>L5X writes booleans as true/false, TRUE/FALSE or 1/0 depending on the element.</summary>
    private static bool Bool(XElement? e, string attribute) =>
        Str(e, attribute) is { } v
        && (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1");

    private static int? FirstInt(XElement scope, string attribute) =>
        scope.DescendantsAndSelf()
             .Select(d => Int(d, attribute))
             .FirstOrDefault(v => v is not null);

    private static string? NullIfZero(string? dimensions) =>
        dimensions is null || dimensions.Trim() == "0" ? null : dimensions;

    private static string? Description(XElement e) => Text(e.Element("Description"));

    /// <summary>
    /// A description or comment: plain CDATA in a single-language project, one
    /// LocalizedDescription/LocalizedComment per language in a multi-language one.
    /// The first language is taken - it is the project's primary.
    /// </summary>
    private static string? Text(XElement? e)
    {
        if (e is null)
        {
            return null;
        }

        XElement? localized = e.Elements().FirstOrDefault(c => c.Name.LocalName.StartsWith("Localized", StringComparison.Ordinal));
        string value = (localized ?? e).Value.Trim();
        return value.Length == 0 ? null : value;
    }
}
