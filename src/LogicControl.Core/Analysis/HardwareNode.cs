using LogicControl.Core.Model;

namespace LogicControl.Core.Analysis;

/// <summary>
/// One module in the rebuilt I/O tree, with its children in slot or address order.
/// </summary>
public sealed class HardwareNode
{
    private readonly List<HardwareNode> _children = [];

    public HardwareNode(ModuleInfo module, HardwareNode? parent)
    {
        Module = module;
        Parent = parent;
        Depth = parent is null ? 0 : parent.Depth + 1;
    }

    public ModuleInfo Module { get; }

    public HardwareNode? Parent { get; }

    public IReadOnlyList<HardwareNode> Children => _children;

    public int Depth { get; }

    /// <summary>
    /// Where it sits on its parent's network: "Slot 3" on a backplane, the IP on Ethernet, and
    /// for the controller its own slot.
    /// </summary>
    public string AddressText
    {
        get
        {
            PortInfo? up = Module.UpstreamPort;
            if (up is { IsEthernet: true, Address: { } ip })
            {
                return ip;
            }

            if (up is { Address: { } slot })
            {
                return $"Slot {slot}";
            }

            // The local controller has no upstream port; its backplane port's address is its slot.
            PortInfo? backplane = Module.Ports.FirstOrDefault(p => !p.IsEthernet && p.Address is not null);
            return backplane is null ? string.Empty : $"Slot {backplane.Address}";
        }
    }

    /// <summary>"Local > ENBT_2 > Rack1_AENT" - how a person would walk to it in Studio 5000.</summary>
    public string Path => Parent is null ? Module.Name : $"{Parent.Path} > {Module.Name}";

    /// <summary>This node and everything under it, depth first.</summary>
    public IEnumerable<HardwareNode> SelfAndDescendants()
    {
        yield return this;
        foreach (HardwareNode child in _children)
        {
            foreach (HardwareNode node in child.SelfAndDescendants())
            {
                yield return node;
            }
        }
    }

    internal void Add(HardwareNode child) => _children.Add(child);

    internal void SortChildren()
    {
        _children.Sort(Compare);
        foreach (HardwareNode child in _children)
        {
            child.SortChildren();
        }
    }

    /// <summary>Backplane before Ethernet, slots numerically, IPs by octet, then by name.</summary>
    private static int Compare(HardwareNode a, HardwareNode b)
    {
        int? slotA = a.Module.Slot;
        int? slotB = b.Module.Slot;

        if (slotA.HasValue && slotB.HasValue && slotA.Value != slotB.Value)
        {
            return slotA.Value.CompareTo(slotB.Value);
        }

        if (slotA.HasValue != slotB.HasValue)
        {
            return slotA.HasValue ? -1 : 1;
        }

        int byIp = IpKey(a.Module.IpAddress).CompareTo(IpKey(b.Module.IpAddress));
        return byIp != 0 ? byIp : string.Compare(a.Module.Name, b.Module.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static long IpKey(string? ip)
    {
        if (ip is null || !System.Net.IPAddress.TryParse(ip, out System.Net.IPAddress? parsed))
        {
            return long.MaxValue;
        }

        byte[] bytes = parsed.GetAddressBytes();
        return bytes.Length == 4 ? ((long)bytes[0] << 24) | ((long)bytes[1] << 16) | ((long)bytes[2] << 8) | bytes[3] : long.MaxValue;
    }
}
