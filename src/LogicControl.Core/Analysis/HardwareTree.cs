using LogicControl.Core.Model;

namespace LogicControl.Core.Analysis;

/// <summary>
/// Puts the I/O configuration back into a tree.
///
/// <para>L5X lists modules flat, each naming its parent. The local controller names itself. A
/// module whose parent is not in the file - which happens in a partial export, or after a module
/// was deleted badly - becomes a root of its own rather than vanishing, so nothing configured is
/// ever missing from the view.</para>
/// </summary>
public static class HardwareTree
{
    public static IReadOnlyList<HardwareNode> Build(PlcProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        Dictionary<string, ModuleInfo> byName = project.Modules
            .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        ILookup<string, ModuleInfo> children = project.Modules
            .Where(m => !m.IsLocal && m.ParentModule is not null)
            .ToLookup(m => m.ParentModule!, StringComparer.OrdinalIgnoreCase);

        var roots = new List<HardwareNode>();
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The controller first, then anything whose parent is missing.
        IEnumerable<ModuleInfo> rootModules = project.Modules.Where(m => m.IsLocal)
            .Concat(project.Modules.Where(m => !m.IsLocal && (m.ParentModule is null || !byName.ContainsKey(m.ParentModule))));

        foreach (ModuleInfo module in rootModules)
        {
            var node = new HardwareNode(module, parent: null);
            roots.Add(node);
            placed.Add(module.Name);
            Attach(node, children, placed);
        }

        // A parent cycle (A under B under A) would leave both unplaced. Show them as roots.
        foreach (ModuleInfo module in project.Modules.Where(m => !placed.Contains(m.Name)))
        {
            var node = new HardwareNode(module, parent: null);
            roots.Add(node);
            placed.Add(module.Name);
            Attach(node, children, placed);
        }

        foreach (HardwareNode root in roots)
        {
            root.SortChildren();
        }

        return roots;
    }

    private static void Attach(HardwareNode node, ILookup<string, ModuleInfo> children, HashSet<string> placed)
    {
        foreach (ModuleInfo child in children[node.Module.Name])
        {
            if (!placed.Add(child.Name))
            {
                continue;
            }

            var childNode = new HardwareNode(child, node);
            node.Add(childNode);
            Attach(childNode, children, placed);
        }
    }
}
