using System.Globalization;
using LogicControl.Core.Analysis;
using LogicControl.Core.Model;

namespace LogicControl.App.ViewModels;

/// <summary>
/// Builds the navigator tree: the controller, then its I/O tree, its tasks with the programs and
/// routines they run, unscheduled programs, AOIs and data types - the order of Studio 5000's
/// Controller Organizer, so nobody has to learn a new map.
/// </summary>
internal static class NavigatorBuilder
{
    public static IReadOnlyList<NavNodeViewModel> Build(ProjectAnalysis analysis, ILookup<string, Finding> bySubject)
    {
        PlcProject p = analysis.Project;
        var roots = new List<NavNodeViewModel>();

        var controller = new NavNodeViewModel(p.Controller.Name, Glyphs.Controller, MainViewModel.OverviewTab.ToString(CultureInfo.InvariantCulture),
            p.Controller.ProcessorType) { IsExpanded = true };
        roots.Add(controller);

        // Hardware
        var io = new NavNodeViewModel("I/O configuration", Glyphs.Network, MainViewModel.HardwareTab.ToString(CultureInfo.InvariantCulture),
            Count(p.Modules.Count)) { IsExpanded = true };
        foreach (HardwareNode root in analysis.Hardware)
        {
            io.Children.Add(HardwareNodeOf(root, bySubject, expand: true));
        }

        controller.Children.Add(io);

        // Tasks, then the programs they schedule, then routines.
        var programsByName = p.Programs
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var scheduled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var tasks = new NavNodeViewModel("Tasks", Glyphs.Folder, badge: Count(p.Tasks.Count)) { IsExpanded = true };
        foreach (TaskInfo task in p.Tasks)
        {
            string badge = task.Type switch
            {
                "PERIODIC" when task.RateMs is { } rate => $"{rate.ToString("0.##", CultureInfo.InvariantCulture)} ms",
                null => string.Empty,
                _ => task.Type.ToLowerInvariant(),
            };

            var taskNode = new NavNodeViewModel(task.Name, Glyphs.Task, badge: badge) { IsExpanded = true };
            foreach (string name in task.ScheduledPrograms)
            {
                if (programsByName.TryGetValue(name, out ProgramInfo? program))
                {
                    scheduled.Add(program.Name);
                    taskNode.Children.Add(ProgramNodeOf(program, bySubject));
                }
            }

            tasks.Children.Add(taskNode);
        }

        controller.Children.Add(tasks);

        List<ProgramInfo> unscheduled = p.Programs.Where(x => !scheduled.Contains(x.Name)).ToList();
        if (unscheduled.Count > 0)
        {
            var folder = new NavNodeViewModel("Unscheduled programs", Glyphs.Folder, badge: Count(unscheduled.Count))
            {
                Level = ViewModels.Level.Warning,
            };
            folder.Children.AddRange(unscheduled.Select(x => ProgramNodeOf(x, bySubject)));
            controller.Children.Add(folder);
        }

        if (p.AddOnInstructions.Count > 0)
        {
            var aois = new NavNodeViewModel("Add-On Instructions", Glyphs.Folder, badge: Count(p.AddOnInstructions.Count));
            aois.Children.AddRange(p.AddOnInstructions.Select(a => new NavNodeViewModel(
                a.Name, a.IsProtected ? Glyphs.Lock : Glyphs.Aoi, a, a.Revision is null ? null : $"v{a.Revision}")));
            controller.Children.Add(aois);
        }

        if (p.DataTypes.Count > 0)
        {
            var types = new NavNodeViewModel("Data types", Glyphs.Folder, badge: Count(p.DataTypes.Count));
            types.Children.AddRange(p.DataTypes.Select(t => new NavNodeViewModel(
                t.Name, Glyphs.DataType, MainViewModel.TagsTab.ToString(CultureInfo.InvariantCulture), $"{t.Members.Count} members")));
            controller.Children.Add(types);
        }

        return roots;
    }

    private static NavNodeViewModel HardwareNodeOf(HardwareNode node, ILookup<string, Finding> bySubject, bool expand)
    {
        ModuleInfo m = node.Module;
        IEnumerable<Finding> findings = bySubject[m.Name];
        if (m.IpAddress is { } ip)
        {
            findings = findings.Concat(bySubject[ip]);
        }

        string glyph = m.IsLocal || m.ProductType == 14 ? Glyphs.Controller
            : m.Ports.Any(x => x.IsEthernet && !x.Upstream) ? Glyphs.Network
            : (m.CatalogNumber ?? string.Empty).Contains("PowerFlex", StringComparison.OrdinalIgnoreCase) ? Glyphs.Drive
            : Glyphs.Module;

        var nav = new NavNodeViewModel($"{m.Name}", glyph, node, node.AddressText)
        {
            Level = ViewModels.Level.Worst(findings),
            IsExpanded = expand,
        };

        foreach (HardwareNode child in node.Children)
        {
            nav.Children.Add(HardwareNodeOf(child, bySubject, expand: node.Depth < 1));
        }

        return nav;
    }

    private static NavNodeViewModel ProgramNodeOf(ProgramInfo program, ILookup<string, Finding> bySubject)
    {
        var node = new NavNodeViewModel(program.Name, Glyphs.Program, badge: program.Disabled ? "disabled" : null)
        {
            IsExpanded = true,
            Level = ViewModels.Level.Worst(bySubject[program.Name]),
        };

        foreach (RoutineInfo routine in program.Routines)
        {
            string glyph = routine.IsProtected ? Glyphs.Lock : routine.Language switch
            {
                RoutineLanguage.Ladder => Glyphs.Ladder,
                RoutineLanguage.StructuredText => Glyphs.Text,
                _ => Glyphs.Block,
            };

            string badge = routine.Language switch
            {
                RoutineLanguage.Ladder => $"{routine.Rungs.Count}",
                RoutineLanguage.StructuredText => "ST",
                RoutineLanguage.FunctionBlock => "FBD",
                RoutineLanguage.Sfc => "SFC",
                _ => string.Empty,
            };

            bool main = string.Equals(routine.Name, program.MainRoutineName, StringComparison.OrdinalIgnoreCase);
            node.Children.Add(new NavNodeViewModel(main ? $"{routine.Name}  (main)" : routine.Name, glyph, routine, badge)
            {
                Level = ViewModels.Level.Worst(bySubject[routine.QualifiedName]),
            });
        }

        return node;
    }

    private static string Count(int n) => n.ToString(CultureInfo.InvariantCulture);
}
