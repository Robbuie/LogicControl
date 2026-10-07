using LogicControl.Core.Model;

namespace LogicControl.Core.Analysis;

/// <summary>
/// The prefix a module's tags carry in logic - the counterpart of
/// <see cref="Logic.TagReference.ModuleOf"/>. See that method for the three shapes.
/// </summary>
public static class ModuleTags
{
    public static string PrefixOf(ModuleInfo module, IReadOnlyDictionary<string, ModuleInfo> byName)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(byName);

        // A module on a backplane is addressed by its slot under whoever owns that chassis: the
        // controller's chassis is always called Local, a remote rack by its adapter's name.
        if (module.Slot is { } slot && module.ParentModule is { } parentName
            && byName.TryGetValue(parentName, out ModuleInfo? parent))
        {
            return parent.IsLocal ? $"Local:{slot}" : $"{parent.Name}:{slot}";
        }

        return module.Name;
    }
}
