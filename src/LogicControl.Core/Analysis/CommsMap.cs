using System.Globalization;
using System.Net;
using LogicControl.Core.Model;

namespace LogicControl.Core.Analysis;

/// <summary>
/// Everything this controller talks to, and how: owned I/O connections, produced and consumed
/// tags, MSG instructions and the GSV reads that watch a module's health.
///
/// <para>Messages are the part the I/O tree does not show. A connection path such as
/// <c>ENBT_2, 2, 192.168.1.40, 1, 0</c> reads as: out through the module ENBT_2, its port 2
/// (Ethernet), to 192.168.1.40, then its backplane (port 1) to slot 0. The far end is the
/// module in the tree that owns that address if there is one, otherwise the address itself.</para>
/// </summary>
public static class CommsMap
{
    public static IReadOnlyList<CommLink> Build(PlcProject project, CrossReference xref, IReadOnlyList<HardwareNode> hardware)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(xref);
        ArgumentNullException.ThrowIfNull(hardware);

        string controller = project.Controller.Name;
        var links = new List<CommLink>();

        Dictionary<string, HardwareNode> nodes = hardware
            .SelectMany(r => r.SelfAndDescendants())
            .GroupBy(n => n.Module.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        Dictionary<string, string> byIp = project.Modules
            .Where(m => m.IpAddress is not null && !m.IsLocal)
            .GroupBy(m => m.IpAddress!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);

        // 1. Owned I/O connections.
        foreach (ModuleInfo module in project.Modules.Where(m => !m.IsLocal && m.Connections.Count > 0))
        {
            nodes.TryGetValue(module.Name, out HardwareNode? node);
            links.Add(new CommLink
            {
                Kind = CommKind.Io,
                From = controller,
                To = module.Name,
                Address = module.IpAddress ?? node?.Path,
                RpiMs = module.Connections.Min(c => c.RpiMs),
                Detail = DescribeConnections(module),
                Source = module.CatalogNumber,
            });
        }

        // 2. Produced and consumed tags.
        foreach (TagInfo tag in project.Tags)
        {
            if (tag.Kind == TagKind.Produced)
            {
                links.Add(new CommLink
                {
                    Kind = CommKind.Produced,
                    From = controller,
                    To = "(consumers)",
                    Detail = $"{tag.DataType}, up to {tag.ProduceCount?.ToString(CultureInfo.InvariantCulture) ?? "?"} consumers",
                    Source = tag.Name,
                });
            }
            else if (tag.Kind == TagKind.Consumed && tag.Consume is { } consume)
            {
                string producer = consume.Producer ?? "(unknown producer)";
                links.Add(new CommLink
                {
                    Kind = CommKind.Consumed,
                    From = producer,
                    To = controller,
                    Address = nodes.TryGetValue(producer, out HardwareNode? p) ? p.Module.IpAddress ?? p.Path : null,
                    RpiMs = consume.RpiMs,
                    Detail = $"{consume.RemoteTag ?? tag.Name} as {tag.Name} ({tag.DataType})"
                        + (consume.Unicast == true ? ", unicast" : string.Empty),
                    Source = tag.Name,
                });
            }
        }

        // 3. Messages - one link per MESSAGE tag, with every place logic fires it.
        Dictionary<string, List<InstructionSite>> fired = xref.Instructions
            .Where(s => s.Instruction.Mnemonic.Equals("MSG", StringComparison.OrdinalIgnoreCase)
                && s.Instruction.Operands.Count > 0)
            .GroupBy(s => BaseOf(s.Instruction.Operands[0]), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (TagInfo tag in project.AllTags.Where(t => t.IsMessage))
        {
            MessageInfo? message = tag.Message;
            MessageTarget target = ParsePath(message?.ConnectionPath, byIp);
            fired.TryGetValue(tag.Name, out List<InstructionSite>? sites);

            links.Add(new CommLink
            {
                Kind = CommKind.Message,
                From = controller,
                To = target.Name,
                Address = target.Address,
                Detail = DescribeMessage(message),
                Source = sites is { Count: > 0 }
                    ? $"{tag.QualifiedName} - fired at {string.Join(", ", sites.Select(s => $"{s.Routine.QualifiedName} {s.LocationText}"))}"
                    : $"{tag.QualifiedName} - never fired",
            });
        }

        // 4. GSV status reads on modules - the logic that notices a dropped connection.
        foreach (InstructionSite site in xref.Instructions.Where(s =>
            s.Instruction.Mnemonic.Equals("GSV", StringComparison.OrdinalIgnoreCase)
            && s.Instruction.Operands.Count >= 3
            && s.Instruction.Operands[0].Equals("Module", StringComparison.OrdinalIgnoreCase)))
        {
            string module = site.Instruction.Operands[1];
            links.Add(new CommLink
            {
                Kind = CommKind.StatusRead,
                From = module,
                To = controller,
                Address = nodes.TryGetValue(module, out HardwareNode? n) ? n.Module.IpAddress ?? n.Path : null,
                Detail = $"{site.Instruction.Operands[2]} into {(site.Instruction.Operands.Count > 3 ? site.Instruction.Operands[3] : "?")}",
                Source = $"{site.Routine.QualifiedName} {site.LocationText}",
            });
        }

        return links;
    }

    private static string BaseOf(string operand)
    {
        int cut = operand.IndexOfAny(['.', '[']);
        return (cut < 0 ? operand : operand[..cut]).Trim();
    }

    private static string DescribeConnections(ModuleInfo module) =>
        string.Join("; ", module.Connections.Select(c =>
        {
            var parts = new List<string>();
            if (c.Type is not null)
            {
                parts.Add(c.Type);
            }

            if (c.InputSize is > 0)
            {
                parts.Add($"in {c.InputSize}B" + (c.InputInstance is { } ii ? $" @ {ii}" : string.Empty));
            }

            if (c.OutputSize is > 0)
            {
                parts.Add($"out {c.OutputSize}B" + (c.OutputInstance is { } oi ? $" @ {oi}" : string.Empty));
            }

            if (c.Unicast == true)
            {
                parts.Add("unicast");
            }

            return parts.Count == 0 ? c.Name ?? "connection" : string.Join(", ", parts);
        }));

    private static string DescribeMessage(MessageInfo? m)
    {
        if (m is null)
        {
            return "MESSAGE tag with no configuration in the export";
        }

        var parts = new List<string> { m.MessageType ?? "MSG" };
        if (m.RemoteElement is not null)
        {
            parts.Add($"remote {m.RemoteElement}");
        }

        if (m.LocalElement is not null)
        {
            parts.Add($"local {m.LocalElement}");
        }

        if (m.RequestedLength is { } length)
        {
            parts.Add($"{length} element{(length == 1 ? string.Empty : "s")}");
        }

        if (m.ServiceCode is not null && (m.MessageType?.Contains("Generic", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            parts.Add($"svc {m.ServiceCode} class {m.ObjectClass} inst {m.Instance} attr {m.Attribute}");
        }

        if (m.Connected)
        {
            parts.Add(m.CacheConnections ? "connected, cached" : "connected");
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// Where a connection path ends. The path alternates module-or-port and address; the last IP
    /// in it is the device on the network, and anything after that is a hop across its backplane.
    /// </summary>
    internal static MessageTarget ParsePath(string? path, IReadOnlyDictionary<string, string> modulesByIp)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new MessageTarget("(no path)", null);
        }

        string[] tokens = path.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        // A single name is a module in this controller's I/O tree - usually another controller
        // added there so its tags can be browsed.
        if (tokens.Length == 1)
        {
            return new MessageTarget(tokens[0], null);
        }

        // Dotted quads only: IPAddress.TryParse would also accept the "1" and "0" of a port and
        // slot as 0.0.0.1 and 0.0.0.0, and the last of those would win.
        int ipIndex = Array.FindLastIndex(tokens, IsDottedQuad);
        if (ipIndex < 0)
        {
            // Backplane only: e.g. "1, 3" - port 1, slot 3 of this chassis.
            return new MessageTarget($"Backplane {string.Join(", ", tokens)}", path);
        }

        string ip = tokens[ipIndex];
        string tail = ipIndex + 2 < tokens.Length && tokens[ipIndex + 1] == "1"
            ? $", slot {tokens[ipIndex + 2]}"
            : string.Empty;

        string name = modulesByIp.TryGetValue(ip, out string? module) ? module : ip;
        return new MessageTarget(name + tail, ip);
    }

    private static bool IsDottedQuad(string token) =>
        token.Count(c => c == '.') == 3 && IPAddress.TryParse(token, out IPAddress? address)
        && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
}

/// <summary>Where a message ends up: a display name and, when known, the IP it reaches.</summary>
public sealed record MessageTarget(string Name, string? Address);
