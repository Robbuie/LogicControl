using System.Globalization;
using LogicControl.Core.Analysis;
using LogicControl.Core.Model;

namespace LogicControl.App.ViewModels;

/// <summary>One module as a row of the hardware grid, indented by its depth in the I/O tree.</summary>
public sealed class HardwareRowViewModel
{
    public HardwareRowViewModel(HardwareNode node, IEnumerable<Finding> findings, int logicReferences)
    {
        ArgumentNullException.ThrowIfNull(node);

        Node = node;
        ModuleInfo m = node.Module;

        Name = m.Name;
        Catalog = m.CatalogNumber ?? string.Empty;
        Address = node.AddressText;
        Revision = m.Revision ?? string.Empty;
        Keying = m.Keying ?? string.Empty;
        Description = m.Description ?? string.Empty;
        Path = node.Path;
        Indent = node.Depth * 18;
        LogicReferences = logicReferences;

        double? rpi = m.Connections.Min(c => c.RpiMs);
        Rpi = rpi is { } r ? r.ToString("0.##", CultureInfo.InvariantCulture) + " ms" : string.Empty;

        Connection = string.Join(", ", m.Connections.Select(c => c.Type ?? c.Name).OfType<string>().Distinct());

        var flags = new List<string>();
        if (m.IsLocal)
        {
            flags.Add("Controller");
        }

        if (m.Inhibited)
        {
            flags.Add("Inhibited");
        }

        if (m.MajorFaultOnConnectionFailure && !m.IsLocal)
        {
            flags.Add("Faults on loss");
        }

        if (m.IsGenericEthernet)
        {
            flags.Add("Generic");
        }

        Flags = string.Join(" · ", flags);

        List<Finding> mine = findings.ToList();
        Findings = mine;
        Level = ViewModels.Level.Worst(mine);
        Kind = KindOf(m);
    }

    public HardwareNode Node { get; }

    public string Name { get; }

    public string Catalog { get; }

    public string Address { get; }

    public string Revision { get; }

    public string Keying { get; }

    public string Rpi { get; }

    public string Connection { get; }

    public string Flags { get; }

    public string Description { get; }

    public string Path { get; }

    /// <summary>Left padding for the name cell - the tree, drawn as indentation in a grid.</summary>
    public double Indent { get; }

    /// <summary>How many times logic touches this module's tags.</summary>
    public int LogicReferences { get; }

    public IReadOnlyList<Finding> Findings { get; }

    public string Level { get; }

    /// <summary>Controller, Adapter, Drive, Generic, IO - picks the row's glyph.</summary>
    public string Kind { get; }

    public string SearchText => $"{Name} {Catalog} {Address} {Description} {Flags}";

    private static string KindOf(ModuleInfo m)
    {
        string catalog = m.CatalogNumber ?? string.Empty;
        if (m.IsLocal || m.ProductType == 14)
        {
            return "Controller";
        }

        if (m.IsGenericEthernet)
        {
            return "Generic";
        }

        if (m.ProductType == 12 || catalog.Contains("-EN", StringComparison.OrdinalIgnoreCase)
            || catalog.Contains("AENT", StringComparison.OrdinalIgnoreCase))
        {
            return "Adapter";
        }

        if (catalog.Contains("PowerFlex", StringComparison.OrdinalIgnoreCase) || m.ProductType is 123 or 143 or 150)
        {
            return "Drive";
        }

        return "IO";
    }
}
