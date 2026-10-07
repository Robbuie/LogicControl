using System.Globalization;
using LogicControl.Core.Analysis;
using LogicControl.Core.Model;

namespace LogicControl.App.ViewModels;

/// <summary>The first tab: what this controller is, at a glance.</summary>
public sealed class OverviewViewModel
{
    public OverviewViewModel(ProjectAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        PlcProject p = analysis.Project;
        ControllerInfo c = p.Controller;

        ControllerName = c.Name;
        ControllerLine = string.Join(" · ", new[]
        {
            c.ProcessorType,
            c.Revision is null ? null : $"v{c.Revision}",
            c.IsSafety ? "GuardLogix" : null,
            p.TargetType is "Controller" or null ? null : $"partial export: {p.TargetType} {p.TargetName}",
        }.OfType<string>());
        ControllerDescription = c.Description ?? string.Empty;
        ExportLine = $"Exported {p.ExportDate ?? "(date not recorded)"} from Studio 5000 v{p.SoftwareRevision ?? "?"}";

        List<ModuleInfo> modules = p.Modules.Where(m => !m.IsLocal).ToList();
        int ethernet = modules.Count(m => m.IpAddress is not null);

        Cards =
        [
            new StatCard("Hardware", Count(modules.Count), "modules",
                $"{ethernet} on Ethernet · {modules.Count - ethernet} in chassis"),
            new StatCard("Communications", Count(analysis.Communications.Count), "links",
                Breakdown(analysis.Communications)),
            new StatCard("Logic", Count(analysis.RungCount), "rungs",
                $"{p.Programs.Count} programs · {p.AllRoutines.Count()} routines · {p.Tasks.Count} tasks"),
            new StatCard("Data", Count(analysis.TagCount), "tags",
                $"{p.DataTypes.Count} UDTs · {p.AddOnInstructions.Count} AOIs"),
        ];

        Errors = analysis.Findings.Count(f => f.Severity == FindingSeverity.Error);
        Warnings = analysis.Findings.Count(f => f.Severity == FindingSeverity.Warning);
        Infos = analysis.Findings.Count(f => f.Severity == FindingSeverity.Info);
        TopFindings = analysis.Findings.Take(6).Select(f => new FindingRowViewModel(f)).ToList();
        Networks = analysis.Hardware
            .SelectMany(r => r.SelfAndDescendants())
            .Where(n => n.Module.Ports.Any(port => port.IsEthernet && !port.Upstream && port.Address is not null))
            .Select(n => new NetworkSummary(
                n.Module.Name,
                n.Module.Ports.First(port => port.IsEthernet && !port.Upstream && port.Address is not null).Address!,
                n.Children.Count(ch => ch.Module.UpstreamPort?.IsEthernet == true)))
            .ToList();
    }

    public string ControllerName { get; }

    public string ControllerLine { get; }

    public string ControllerDescription { get; }

    public string ExportLine { get; }

    public IReadOnlyList<StatCard> Cards { get; }

    public int Errors { get; }

    public int Warnings { get; }

    public int Infos { get; }

    public IReadOnlyList<FindingRowViewModel> TopFindings { get; }

    /// <summary>Each Ethernet port that offers a network to children: the controller's own and each bridge's.</summary>
    public IReadOnlyList<NetworkSummary> Networks { get; }

    private static string Count(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

    private static string Breakdown(IReadOnlyList<CommLink> links) =>
        string.Join(" · ", links.GroupBy(l => l.Kind).OrderBy(g => g.Key)
            .Select(g => $"{g.Count()} {Short(g.Key)}"));

    private static string Short(CommKind kind) => kind switch
    {
        CommKind.Io => "I/O",
        CommKind.Produced => "produced",
        CommKind.Consumed => "consumed",
        CommKind.Message => "MSG",
        CommKind.StatusRead => "GSV",
        _ => kind.ToString(),
    };
}

/// <summary>One of the four headline numbers.</summary>
public sealed record StatCard(string Title, string Value, string Unit, string Detail);

/// <summary>An Ethernet network this controller reaches: through which module, and how many devices are on it.</summary>
public sealed record NetworkSummary(string Via, string Address, int Devices);
