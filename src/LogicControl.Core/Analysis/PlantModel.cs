using System.Globalization;
using LogicControl.Core.Logic;
using LogicControl.Core.Model;

namespace LogicControl.Core.Analysis;

public enum PlantNodeKind
{
    /// <summary>A controller - one opened project.</summary>
    Controller,

    /// <summary>A module that carries a network or a backplane onward: an EN2T, an AENTR.</summary>
    Bridge,

    /// <summary>Anything else in an I/O tree: a card, a drive, a Generic Ethernet device.</summary>
    Device,

    /// <summary>A module in one project's I/O tree that is another opened project's controller.</summary>
    Peer,

    /// <summary>An address a message goes to that no opened project has in its I/O tree.</summary>
    External,
}

public enum PlantLinkKind
{
    /// <summary>Parent to child in an I/O tree, with no connection of its own (a backplane, a bridge).</summary>
    Tree,

    /// <summary>Parent to child where the controller owns an I/O connection to the child.</summary>
    Io,

    /// <summary>A produced tag consumed by another controller.</summary>
    Produced,

    /// <summary>A MSG instruction.</summary>
    Message,

    /// <summary>A module in one tree that is the controller of another opened project.</summary>
    SameDevice,
}

/// <summary>One box in the system view.</summary>
public sealed record PlantNode
{
    /// <summary>Unique within the plant: "0:ENBT_2" (project index and module), "ext:10.0.0.9".</summary>
    public required string Id { get; init; }

    public required PlantNodeKind Kind { get; init; }

    public required string Name { get; init; }

    public string? Catalog { get; init; }

    public string? Address { get; init; }

    /// <summary>Which opened project it belongs to - 0 is the one on the other tabs - or -1 for an external address.</summary>
    public int Project { get; init; } = -1;

    /// <summary>The I/O tree node behind it, for opening it on the Hardware tab.</summary>
    public HardwareNode? Hardware { get; init; }

    public string? ParentId { get; init; }

    /// <summary>For a <see cref="PlantNodeKind.Peer"/>: the project whose controller it is.</summary>
    public int? PeerOf { get; init; }

    public bool Inhibited { get; init; }
}

/// <summary>One line in the system view: what joins two boxes, and a few words on it.</summary>
public sealed record PlantLink(string From, string To, PlantLinkKind Kind, string Label, string? Detail = null);

/// <summary>
/// Every opened controller, its I/O tree and how they all talk to each other - the plant as a
/// whole, joined on the addresses in their I/O trees, their produced and consumed tags and the
/// paths of their messages.
///
/// <para><b>How two projects are joined.</b> A controller is known on the network by the
/// addresses of its own chassis: its embedded Ethernet port and the Ethernet bridges in its local
/// rack. A module in another project's I/O tree at one of those addresses, or under a bridge at
/// one, or simply named like the controller, is that controller seen from outside - a
/// <see cref="PlantNodeKind.Peer"/>. A consumed tag whose producer resolves to a peer is the
/// other project's produced tag; a message whose path ends at one of its addresses is a read or
/// write of its tags. Both can then be checked against what the other project actually has,
/// which no single export can do (the LC-PLT rules).</para>
/// </summary>
public sealed class PlantModel
{
    private PlantModel(IReadOnlyList<ProjectAnalysis> projects, List<PlantNode> nodes, List<PlantLink> links, List<Finding> findings)
    {
        Projects = projects;
        Nodes = nodes;
        Links = links;
        Findings = findings;
    }

    public IReadOnlyList<ProjectAnalysis> Projects { get; }

    public IReadOnlyList<PlantNode> Nodes { get; }

    public IReadOnlyList<PlantLink> Links { get; }

    /// <summary>What only shows when the controllers are looked at together. Empty for one project.</summary>
    public IReadOnlyList<Finding> Findings { get; }

    public PlantNode? Find(string id) => Nodes.FirstOrDefault(n => n.Id == id);

    public static PlantModel Build(IReadOnlyList<ProjectAnalysis> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        var nodes = new List<PlantNode>();
        var links = new List<PlantLink>();
        var findings = new List<Finding>();

        // Each controller's addresses, for recognising it in another project's tree.
        List<HashSet<string>> addresses = projects.Select(AddressesOf).ToList();

        // 1. Every I/O tree, with peers recognised.
        var nodeOfModule = new Dictionary<(int, string), PlantNode>();
        for (int p = 0; p < projects.Count; p++)
        {
            ProjectAnalysis a = projects[p];
            foreach (HardwareNode root in a.Hardware)
            {
                foreach (HardwareNode h in root.SelfAndDescendants())
                {
                    ModuleInfo m = h.Module;
                    int? peer = h.Parent is null ? null : PeerOf(h, p, projects, addresses);
                    var node = new PlantNode
                    {
                        Id = Id(p, m.Name),
                        Kind = h.Parent is null ? PlantNodeKind.Controller
                            : peer is not null ? PlantNodeKind.Peer
                            : h.Children.Count > 0
                              || (m.Ports.Any(x => !x.Upstream) && !m.IsGenericEthernet && !IsController(m) && m.Connections.Count == 0)
                                ? PlantNodeKind.Bridge
                                : PlantNodeKind.Device,
                        Name = h.Parent is null ? a.Project.Controller.Name : m.Name,
                        Catalog = h.Parent is null ? a.Project.Controller.ProcessorType ?? m.CatalogNumber : m.CatalogNumber,
                        Address = m.IpAddress ?? (h.Parent is null ? null : h.AddressText),
                        Project = p,
                        Hardware = h,
                        ParentId = h.Parent is null ? null : Id(p, h.Parent.Module.Name),
                        PeerOf = peer,
                        Inhibited = m.Inhibited,
                    };
                    nodes.Add(node);
                    nodeOfModule[(p, m.Name.ToUpperInvariant())] = node;

                    if (h.Parent is not null)
                    {
                        bool owned = m.Connections.Count > 0;
                        string label = owned && m.Connections.Min(c => c.RpiMs) is { } rpi
                            ? $"{Ms(rpi)} ms"
                            : h.AddressText;
                        links.Add(new PlantLink(Id(p, h.Parent.Module.Name), node.Id, owned ? PlantLinkKind.Io : PlantLinkKind.Tree, label));
                    }

                    if (peer is { } other)
                    {
                        links.Add(new PlantLink(node.Id, ControllerId(projects, other), PlantLinkKind.SameDevice,
                            $"is {projects[other].Project.Controller.Name}"));
                    }
                }
            }
        }

        // 2. Produced and consumed tags, between controllers when the producer is opened too.
        for (int p = 0; p < projects.Count; p++)
        {
            ProjectAnalysis consumer = projects[p];
            foreach (TagInfo tag in consumer.Project.Tags.Where(t => t.Kind == TagKind.Consumed && t.Consume is not null))
            {
                ConsumeInfo c = tag.Consume!;
                PlantNode? producerNode = c.Producer is null ? null : nodeOfModule.GetValueOrDefault((p, c.Producer.ToUpperInvariant()));
                int? producer = producerNode?.PeerOf;
                string label = $"{c.RemoteTag ?? tag.Name} → {tag.Name}";
                string detail = $"{tag.DataType}, RPI {(c.RpiMs is { } r ? Ms(r) : "?")} ms";

                if (producer is { } b)
                {
                    links.Add(new PlantLink(ControllerId(projects, b), ControllerId(projects, p), PlantLinkKind.Produced, label, detail));
                    CheckConsumed(projects[b], consumer, tag, findings);
                }
                else if (producerNode is not null)
                {
                    links.Add(new PlantLink(producerNode.Id, ControllerId(projects, p), PlantLinkKind.Produced, label, detail));
                }
            }
        }

        // 3. Messages, to peers, to modules in the tree, or out to an address nobody has.
        for (int p = 0; p < projects.Count; p++)
        {
            ProjectAnalysis a = projects[p];
            Dictionary<string, string> byIp = a.Project.Modules
                .Where(m => m.IpAddress is not null && !m.IsLocal)
                .GroupBy(m => m.IpAddress!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);

            foreach (TagInfo tag in a.Project.AllTags.Where(t => t.IsMessage && t.Message?.ConnectionPath is not null))
            {
                MessageTarget target = CommsMap.ParsePath(tag.Message!.ConnectionPath, byIp);
                string label = $"MSG {tag.Name}";
                string detail = $"{tag.Message.MessageType}{(tag.Message.RemoteElement is { } re ? $", remote {re}" : string.Empty)}";

                int? peer = target.Address is { } ip ? addresses.FindIndex(set => set.Contains(ip)) : null;
                if (peer is < 0)
                {
                    peer = null;
                }

                string? moduleName = target.Address is { } ip2 && byIp.TryGetValue(ip2, out string? mn) ? mn
                    : target.Address is null ? tag.Message.ConnectionPath!.Split(',')[0].Trim() : null;
                PlantNode? inTree = moduleName is null ? null : nodeOfModule.GetValueOrDefault((p, moduleName.ToUpperInvariant()));
                peer ??= inTree?.PeerOf;

                if (peer is { } b && b != p)
                {
                    links.Add(new PlantLink(ControllerId(projects, p), ControllerId(projects, b), PlantLinkKind.Message, label, detail));
                    CheckMessage(a, tag, projects[b], findings);
                }
                else if (inTree is not null)
                {
                    links.Add(new PlantLink(ControllerId(projects, p), inTree.Id, PlantLinkKind.Message, label, detail));
                }
                else if (target.Address is { } external)
                {
                    string id = $"ext:{external}";
                    if (nodes.All(n => n.Id != id))
                    {
                        nodes.Add(new PlantNode { Id = id, Kind = PlantNodeKind.External, Name = external, Address = external });
                    }

                    links.Add(new PlantLink(ControllerId(projects, p), id, PlantLinkKind.Message, label, detail));
                }
            }
        }

        // 4. Rules that need more than one controller.
        if (projects.Count > 1)
        {
            ProducerOverbooked(projects, nodes, findings);
            SharedOwnership(projects, findings);
        }

        return new PlantModel(projects, nodes, links, findings
            .OrderBy(f => f.Severity)
            .ThenBy(f => f.Subject, StringComparer.OrdinalIgnoreCase)
            .ToList());
    }

    // ------------------------------------------------------------------ joining

    /// <summary>
    /// A controller's addresses: its own Ethernet ports and those of the bridges in its local chassis.
    /// </summary>
    internal static HashSet<string> AddressesOf(ProjectAnalysis analysis)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (HardwareNode root in analysis.Hardware)
        {
            foreach (string? ip in root.Module.Ports.Where(p => p.IsEthernet).Select(p => p.Address))
            {
                if (!string.IsNullOrWhiteSpace(ip))
                {
                    set.Add(ip);
                }
            }

            foreach (HardwareNode child in root.Children.Where(c => c.Module.Slot is not null))
            {
                foreach (string? ip in child.Module.Ports.Where(p => p.IsEthernet && !p.Upstream).Select(p => p.Address))
                {
                    if (!string.IsNullOrWhiteSpace(ip))
                    {
                        set.Add(ip);
                    }
                }
            }
        }

        return set;
    }

    /// <summary>
    /// Which other opened project this module is the controller of: its address (or that of the
    /// nearest bridge above it) is one of that controller's, or - for a controller module - its name
    /// is that controller's.
    /// </summary>
    private static int? PeerOf(HardwareNode node, int self, IReadOnlyList<ProjectAnalysis> projects, List<HashSet<string>> addresses)
    {
        for (int other = 0; other < projects.Count; other++)
        {
            if (other == self)
            {
                continue;
            }

            if (string.Equals(node.Module.Name, projects[other].Project.Controller.Name, StringComparison.OrdinalIgnoreCase))
            {
                return other;
            }

            // The module's own address - a controller with an embedded port, or a bridge in its chassis.
            if (node.Module.IpAddress is { } ip && addresses[other].Contains(ip) && IsController(node.Module))
            {
                return other;
            }

            // A controller in a slot under a bridge that is in the other controller's chassis.
            if (IsController(node.Module) && node.Parent?.Module.IpAddress is { } bridgeIp && addresses[other].Contains(bridgeIp))
            {
                return other;
            }
        }

        return null;
    }

    /// <summary>Product type 14 is a programmable logic controller in the CIP device profiles.</summary>
    private static bool IsController(ModuleInfo module) =>
        module.ProductType == 14
        || (module.CatalogNumber?.Contains("-L", StringComparison.OrdinalIgnoreCase) ?? false)
           && (module.CatalogNumber?.StartsWith("17", StringComparison.Ordinal) ?? false);

    private static string Id(int project, string module) => $"{project.ToString(CultureInfo.InvariantCulture)}:{module}";

    private static string ControllerId(IReadOnlyList<ProjectAnalysis> projects, int project) =>
        Id(project, projects[project].Hardware.FirstOrDefault()?.Module.Name ?? projects[project].Project.Controller.Name);

    // ------------------------------------------------------------------ rules

    /// <summary>LC-PLT-001, -002, -003: a consumed tag checked against the producer's own tag.</summary>
    private static void CheckConsumed(ProjectAnalysis producer, ProjectAnalysis consumer, TagInfo consumed, List<Finding> findings)
    {
        ConsumeInfo c = consumed.Consume!;
        string remote = c.RemoteTag ?? consumed.Name;
        string where = $"{consumer.Project.Controller.Name}.{consumed.Name}";
        TagInfo? produced = producer.Project.Tags.FirstOrDefault(t => string.Equals(t.Name, remote, StringComparison.OrdinalIgnoreCase));

        if (produced is null || produced.Kind != TagKind.Produced)
        {
            findings.Add(new Finding(
                FindingSeverity.Error, "LC-PLT-001", "Plant", where,
                $"{consumer.Project.Controller.Name} consumes {remote} from {producer.Project.Controller.Name}, which "
                + (produced is null ? "has no controller tag by that name." : $"has {remote} as an ordinary tag, not a produced one.")
                + " The connection will not open."));
            return;
        }

        if (!string.Equals(produced.DataType, consumed.DataType, StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new Finding(
                FindingSeverity.Error, "LC-PLT-003", "Plant", where,
                $"{consumed.Name} is a {consumed.DataType} but {producer.Project.Controller.Name} produces {remote} as a {produced.DataType}. "
                + "The connection is refused on a size mismatch."));
        }

        if (c.RpiMs is { } rpi && (produced.ProduceMinRpiMs is { } min && rpi < min || produced.ProduceMaxRpiMs is { } max && rpi > max))
        {
            findings.Add(new Finding(
                FindingSeverity.Warning, "LC-PLT-002", "Plant", where,
                $"{consumed.Name} asks for {remote} every {Ms(rpi)} ms, outside the {Ms(produced.ProduceMinRpiMs ?? 0)}-{Ms(produced.ProduceMaxRpiMs ?? 0)} ms "
                + $"{producer.Project.Controller.Name} allows. The connection is refused."));
        }
    }

    /// <summary>LC-PLT-004: more controllers consume a produced tag than it allows.</summary>
    private static void ProducerOverbooked(IReadOnlyList<ProjectAnalysis> projects, List<PlantNode> nodes, List<Finding> findings)
    {
        for (int b = 0; b < projects.Count; b++)
        {
            foreach (TagInfo produced in projects[b].Project.Tags.Where(t => t.Kind == TagKind.Produced && t.ProduceCount is not null))
            {
                List<string> consumers = [];
                for (int p = 0; p < projects.Count; p++)
                {
                    if (p == b)
                    {
                        continue;
                    }

                    foreach (TagInfo consumed in projects[p].Project.Tags.Where(t => t.Kind == TagKind.Consumed && t.Consume?.Producer is not null))
                    {
                        PlantNode? producerNode = nodes.FirstOrDefault(n => n.Project == p
                            && string.Equals(n.Hardware?.Module.Name, consumed.Consume!.Producer, StringComparison.OrdinalIgnoreCase));
                        if (producerNode?.PeerOf == b
                            && string.Equals(consumed.Consume!.RemoteTag ?? consumed.Name, produced.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            consumers.Add($"{projects[p].Project.Controller.Name}.{consumed.Name}");
                        }
                    }
                }

                if (consumers.Count > produced.ProduceCount)
                {
                    findings.Add(new Finding(
                        FindingSeverity.Warning, "LC-PLT-004", "Plant", $"{projects[b].Project.Controller.Name}.{produced.Name}",
                        $"{produced.Name} allows {produced.ProduceCount!.Value.ToString(CultureInfo.InvariantCulture)} consumers but "
                        + $"{consumers.Count.ToString(CultureInfo.InvariantCulture)} consume it ({string.Join(", ", consumers)}). The last to connect is refused - "
                        + "raise the consumer count on the produced tag."));
                }
            }
        }
    }

    /// <summary>LC-PLT-005: a data-table message names a tag the target controller does not have.</summary>
    private static void CheckMessage(ProjectAnalysis from, TagInfo message, ProjectAnalysis to, List<Finding> findings)
    {
        MessageInfo m = message.Message!;
        if (m.RemoteElement is not { Length: > 0 } remote
            || !(m.MessageType?.Contains("Data Table", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return;
        }

        string? name = TagReference.BaseNames(remote).FirstOrDefault();
        if (name is null || to.Project.Tags.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        findings.Add(new Finding(
            FindingSeverity.Error, "LC-PLT-005", "Plant", $"{from.Project.Controller.Name}.{message.Name}",
            $"{message.Name} {(m.MessageType!.Contains("Write", StringComparison.OrdinalIgnoreCase) ? "writes" : "reads")} {remote} in "
            + $"{to.Project.Controller.Name}, which has no controller tag {name}. The message errors every time it runs."));
    }

    /// <summary>LC-PLT-006: two controllers each own an output connection to the same device.</summary>
    private static void SharedOwnership(IReadOnlyList<ProjectAnalysis> projects, List<Finding> findings)
    {
        var owners = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (int p = 0; p < projects.Count; p++)
        {
            foreach (ModuleInfo m in projects[p].Project.Modules.Where(m => !m.IsLocal && !m.Inhibited && m.IpAddress is not null
                && m.Connections.Any(c => (c.OutputSize ?? 0) > 0)))
            {
                if (!owners.TryGetValue(m.IpAddress!, out List<string>? list))
                {
                    list = [];
                    owners[m.IpAddress!] = list;
                }

                list.Add($"{projects[p].Project.Controller.Name} ({m.Name})");
            }
        }

        foreach ((string ip, List<string> list) in owners.Where(o => o.Value.Select(v => v[..v.IndexOf(' ', StringComparison.Ordinal)]).Distinct().Count() > 1))
        {
            findings.Add(new Finding(
                FindingSeverity.Error, "LC-PLT-006", "Plant", ip,
                $"{string.Join(" and ", list)} each own an output connection to {ip}. A device takes one owner: whichever connects "
                + "second is refused, and which one that is depends on power-up order."));
        }
    }

    private static string Ms(double ms) => ms.ToString("0.###", CultureInfo.InvariantCulture);
}
