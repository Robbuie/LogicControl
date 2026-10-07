using LogicControl.Core.Model;

namespace LogicControl.Core.Authoring;

/// <summary>
/// Drafts made from what an export already contains - the starting point for changing something
/// in the open project, and the "before" side of every comparison with it.
/// </summary>
public static class DraftsFromProject
{
    public static RoutineDraft Routine(RoutineInfo routine)
    {
        ArgumentNullException.ThrowIfNull(routine);
        return new RoutineDraft
        {
            Name = routine.Name,
            Program = routine.Owner,
            Description = routine.Description,
            Rungs = routine.Rungs.Select(r => new RungDraft(r.Text, r.Comment)).ToList(),
        };
    }

    public static AoiDraft Aoi(AoiInfo aoi)
    {
        ArgumentNullException.ThrowIfNull(aoi);
        return new AoiDraft
        {
            Name = aoi.Name,
            Revision = aoi.Revision ?? "1.0",
            Description = aoi.Description,
            Parameters = aoi.Parameters
                .Where(p => p.Name is not ("EnableIn" or "EnableOut"))
                .Select(p => new AoiParameterDraft(p.Name, p.DataType ?? "BOOL", p.Usage ?? "Input", p.Required, p.Description))
                .ToList(),
            LocalTags = aoi.LocalTags.Select(l => new AoiLocalTagDraft(l.Name, l.DataType ?? "DINT", l.Description)).ToList(),
            Logic = aoi.Routines.FirstOrDefault(r => string.Equals(r.Name, "Logic", StringComparison.OrdinalIgnoreCase))?
                .Rungs.Select(r => new RungDraft(r.Text, r.Comment)).ToList() ?? [],
        };
    }

    public static UdtDraft DataType(DataTypeInfo type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return new UdtDraft
        {
            Name = type.Name,
            Description = type.Description,
            Members = type.Members.Select(m => new MemberDraft(m.Name, m.DataType ?? "DINT", m.Description, m.Dimension)).ToList(),
        };
    }

    public static TagDraft Tag(TagInfo tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return new TagDraft
        {
            Name = tag.Name,
            Program = tag.Scope,
            DataType = tag.DataType ?? "DINT",
            Dimensions = tag.Dimensions,
            Description = tag.Description,
            AliasFor = tag.AliasFor,
            Constant = tag.Constant,
            ExternalAccess = tag.ExternalAccess ?? "Read/Write",
        };
    }

    public static ProgramDraft Program(ProgramInfo program, PlcProject project)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(project);
        return new ProgramDraft
        {
            Name = program.Name,
            Description = program.Description,
            MainRoutineName = program.MainRoutineName,
            Task = project.Tasks.FirstOrDefault(t => t.ScheduledPrograms.Contains(program.Name, StringComparer.OrdinalIgnoreCase))?.Name,
        };
    }

    public static ModuleDraft? Module(ModuleInfo module)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (!module.IsGenericEthernet)
        {
            return null;
        }

        ConnectionInfo? c = module.Connections.FirstOrDefault();
        return new ModuleDraft
        {
            Name = module.Name,
            Description = module.Description,
            ParentModule = module.ParentModule ?? "Local",
            ParentPortId = module.ParentPortId ?? 2,
            IpAddress = module.IpAddress ?? string.Empty,
            Format = ModuleFormats.FromCommMethod(module.CommMethod),
            InputInstance = c?.InputInstance ?? 0,
            InputSize = ModuleFormats.Elements(c?.InputSize ?? 0, ModuleFormats.FromCommMethod(module.CommMethod)),
            OutputInstance = c?.OutputInstance ?? 0,
            OutputSize = ModuleFormats.Elements(c?.OutputSize ?? 0, ModuleFormats.FromCommMethod(module.CommMethod)),
            ConfigInstance = module.ConfigInstance ?? 0,
            ConfigSize = module.ConfigSize ?? 0,
            RpiMs = c?.RpiMs ?? 10,
            Unicast = c?.Unicast ?? true,
        };
    }

    /// <summary>
    /// The project's own version of everything <paramref name="set"/> drafts that is also in the
    /// project: the "before" of a comparison. Drafts of new things have no counterpart and are left out.
    /// </summary>
    public static DevelopmentSet Counterparts(PlcProject project, DevelopmentSet set)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(set);

        static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        var before = new DevelopmentSet
        {
            ControllerName = set.ControllerName,
            SoftwareRevision = set.SoftwareRevision,
        };

        foreach (UdtDraft udt in set.DataTypes)
        {
            if (project.DataTypes.FirstOrDefault(d => Same(d.Name, udt.Name)) is { } found)
            {
                before.DataTypes.Add(DataType(found));
            }
        }

        foreach (AoiDraft aoi in set.AddOnInstructions)
        {
            if (project.AddOnInstructions.FirstOrDefault(a => Same(a.Name, aoi.Name)) is { } found)
            {
                before.AddOnInstructions.Add(Aoi(found));
            }
        }

        foreach (ProgramDraft program in set.Programs)
        {
            if (project.Programs.FirstOrDefault(p => Same(p.Name, program.Name)) is { } found)
            {
                before.Programs.Add(Program(found, project));
            }
        }

        foreach (RoutineDraft routine in set.Routines)
        {
            if (project.Programs.FirstOrDefault(p => Same(p.Name, routine.Program))?.Routines
                .FirstOrDefault(r => Same(r.Name, routine.Name)) is { } found)
            {
                before.Routines.Add(Routine(found));
            }
        }

        foreach (TagDraft tag in set.Tags)
        {
            if (project.AllTags.FirstOrDefault(t => Same(t.Name, tag.Name) && Same(t.Scope, tag.Program)) is { } found)
            {
                before.Tags.Add(Tag(found));
            }
        }

        foreach (ModuleDraft module in set.Modules)
        {
            if (project.Modules.FirstOrDefault(m => Same(m.Name, module.Name)) is { } found && Module(found) is { } draft)
            {
                before.Modules.Add(draft);
            }
        }

        return before;
    }
}
