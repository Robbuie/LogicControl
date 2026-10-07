namespace LogicControl.Core.Model;

/// <summary>
/// A port on a module. A backplane port's address is a slot; an Ethernet port's is an IP.
/// <see cref="Upstream"/> marks the port that connects the module to its parent; the others are
/// networks it offers to its children.
/// </summary>
public sealed record PortInfo(int Id, string? Type, string? Address, bool Upstream)
{
    public bool IsEthernet => Type is not null && Type.Contains("Ethernet", StringComparison.OrdinalIgnoreCase);
}
