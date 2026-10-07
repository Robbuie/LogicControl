namespace LogicControl.Core.Model;

/// <summary>
/// One entry in the I/O configuration tree: the controller's own module, a chassis card, an
/// Ethernet adapter, a drive, a remote rack's I/O, or a Generic Ethernet module.
///
/// <para>The tree is stored flat in L5X. Each module names its parent and which of the parent's
/// ports it hangs off (<see cref="ParentModule"/>, <see cref="ParentPortId"/>), and its own
/// upstream port carries its address on that parent's network - a slot number on a backplane, an
/// IP address on Ethernet. <c>Analysis.HardwareTree</c> puts the tree back together.</para>
/// </summary>
public sealed record ModuleInfo
{
    public required string Name { get; init; }

    /// <summary>e.g. 1756-EN2T, 1734-AENTR, ETHERNET-MODULE for a Generic Ethernet module.</summary>
    public string? CatalogNumber { get; init; }

    public int? Vendor { get; init; }

    public int? ProductType { get; init; }

    public int? ProductCode { get; init; }

    public int? MajorRevision { get; init; }

    public int? MinorRevision { get; init; }

    /// <summary>The module this one is plugged into or networked under. The local module names itself.</summary>
    public string? ParentModule { get; init; }

    /// <summary>Which port on the parent this module hangs off.</summary>
    public int? ParentPortId { get; init; }

    public bool Inhibited { get; init; }

    /// <summary>"Major fault on controller if connection fails while in Run mode".</summary>
    public bool MajorFaultOnConnectionFailure { get; init; }

    /// <summary>Electronic keying: ExactMatch, CompatibleModule or Disabled.</summary>
    public string? Keying { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<PortInfo> Ports { get; init; } = [];

    public IReadOnlyList<ConnectionInfo> Connections { get; init; } = [];

    /// <summary>Generic Ethernet modules: the configuration assembly instance and size.</summary>
    public int? ConfigInstance { get; init; }

    public int? ConfigSize { get; init; }

    /// <summary>"33.11", or null when the file did not say.</summary>
    public string? Revision => MajorRevision is { } major ? $"{major}.{MinorRevision ?? 0}" : null;

    /// <summary>True for the controller's own entry in the tree, which is its own parent.</summary>
    public bool IsLocal => string.Equals(Name, ParentModule, StringComparison.Ordinal);

    /// <summary>The port that connects this module upward - its address on the parent's network.</summary>
    public PortInfo? UpstreamPort => Ports.FirstOrDefault(p => p.Upstream);

    /// <summary>Its IP address if it has an Ethernet port with one, upstream first.</summary>
    public string? IpAddress =>
        Ports.Where(p => p.IsEthernet && !string.IsNullOrWhiteSpace(p.Address))
             .OrderByDescending(p => p.Upstream)
             .Select(p => p.Address)
             .FirstOrDefault();

    /// <summary>Its slot, when its upstream port is a backplane.</summary>
    public int? Slot =>
        UpstreamPort is { IsEthernet: false, Address: { } address } && int.TryParse(address, out int slot)
            ? slot
            : null;

    public bool IsGenericEthernet =>
        string.Equals(CatalogNumber, "ETHERNET-MODULE", StringComparison.OrdinalIgnoreCase);
}
