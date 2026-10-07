using LogicControl.Core.Logic;
using LogicControl.Core.Model;

namespace LogicControl.Core.Authoring;

/// <summary>
/// What a draft can refer to: the open project's types, AOIs, tags, programs and routines, with the
/// drafts layered on top - a draft of the same name wins, because it is what will be imported.
///
/// <para>Built once per check. Every lookup ignores case, as Logix does.</para>
/// </summary>
public sealed class DraftContext
{
    private readonly Dictionary<string, Dictionary<string, string>> _udtMembers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, InstructionShape> _aoiShapes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> _aoiMembers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _controllerTags = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string?>> _programTags = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _routines = new(StringComparer.OrdinalIgnoreCase);

    public DraftContext(DevelopmentSet set, PlcProject? project = null)
    {
        ArgumentNullException.ThrowIfNull(set);
        Set = set;
        Project = project;

        if (project is not null)
        {
            foreach (DataTypeInfo t in project.DataTypes)
            {
                _udtMembers[t.Name] = t.Members
                    .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().DataType ?? string.Empty, StringComparer.OrdinalIgnoreCase);
            }

            foreach (AoiInfo a in project.AddOnInstructions)
            {
                _aoiShapes[a.Name] = InstructionShape.ForAoi(a.Name, a.CallParameters.Select(p => p.Name), a.Description);
                _aoiMembers[a.Name] = a.Parameters
                    .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().DataType ?? string.Empty, StringComparer.OrdinalIgnoreCase);
            }

            foreach (TagInfo t in project.Tags)
            {
                _controllerTags[t.Name] = t.DataType;
            }

            foreach (ProgramInfo p in project.Programs)
            {
                Tags(p.Name);
                foreach (TagInfo t in p.Tags)
                {
                    _programTags[p.Name][t.Name] = t.DataType;
                }

                foreach (RoutineInfo r in p.Routines)
                {
                    Routines(p.Name).Add(r.Name);
                }
            }
        }

        foreach (UdtDraft d in set.DataTypes)
        {
            _udtMembers[d.Name] = d.Members
                .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().DataType, StringComparer.OrdinalIgnoreCase);
        }

        foreach (AoiDraft a in set.AddOnInstructions)
        {
            _aoiShapes[a.Name] = InstructionShape.ForAoi(a.Name, a.Parameters.Where(p => p.IsRequired).Select(p => p.Name), a.Description);
            var members = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["EnableIn"] = "BOOL",
                ["EnableOut"] = "BOOL",
            };

            foreach (AoiParameterDraft p in a.Parameters)
            {
                members[p.Name] = p.DataType;
            }

            _aoiMembers[a.Name] = members;
        }

        foreach (ProgramDraft p in set.Programs)
        {
            Tags(p.Name);
            Routines(p.Name);
        }

        foreach (TagDraft t in set.Tags)
        {
            if (t.Program is null)
            {
                _controllerTags[t.Name] = t.AliasFor is null ? t.DataType : null;
            }
            else
            {
                Tags(t.Program)[t.Name] = t.AliasFor is null ? t.DataType : null;
            }
        }

        foreach (RoutineDraft r in set.Routines)
        {
            Routines(r.Program).Add(r.Name);
        }
    }

    public DevelopmentSet Set { get; }

    public PlcProject? Project { get; }

    /// <summary>Every UDT name, project and draft, for the type pickers.</summary>
    public IEnumerable<string> UserTypes => _udtMembers.Keys;

    /// <summary>Every AOI name - an AOI is also a data type for its instance tags.</summary>
    public IEnumerable<string> AoiNames => _aoiShapes.Keys;

    public IEnumerable<string> ProgramNames => _programTags.Keys;

    public bool ProgramExists(string? program) => program is not null && _programTags.ContainsKey(program);

    public bool RoutineExists(string program, string routine) =>
        _routines.TryGetValue(program, out HashSet<string>? names) && names.Contains(routine);

    /// <summary>True for a built-in type, a UDT or an AOI.</summary>
    public bool TypeExists(string? type) =>
        type is not null && (LogixTypes.IsBuiltIn(type) || _udtMembers.ContainsKey(type) || _aoiShapes.ContainsKey(type));

    public bool IsUserType(string? type) => type is not null && _udtMembers.ContainsKey(type);

    public IReadOnlyDictionary<string, string>? UdtMembers(string type) =>
        _udtMembers.TryGetValue(type, out Dictionary<string, string>? members) ? members : null;

    /// <summary>
    /// The members addressable on a value of <paramref name="type"/>: a UDT's members, a built-in
    /// structure's, or an AOI's parameters. Null when the type is atomic or unknown.
    /// </summary>
    public IEnumerable<string>? MembersOf(string? type)
    {
        if (type is null)
        {
            return null;
        }

        if (_udtMembers.TryGetValue(type, out Dictionary<string, string>? udt))
        {
            return udt.Keys;
        }

        if (_aoiMembers.TryGetValue(type, out Dictionary<string, string>? aoi))
        {
            return aoi.Keys;
        }

        return LogixTypes.MembersOf(type);
    }

    /// <summary>The built-in instruction table, then the AOIs - drafts and project.</summary>
    public InstructionShape? ShapeOf(string mnemonic) =>
        InstructionSignatures.Find(mnemonic) ?? (_aoiShapes.TryGetValue(mnemonic, out InstructionShape? s) ? s : null);

    /// <summary>
    /// Whether a tag is declared where a rung in <paramref name="program"/> can see it, and its
    /// type: program scope first, then controller scope. An alias reports a null type.
    /// </summary>
    public bool TryFindTag(string? program, string name, out string? type)
    {
        if (program is not null && _programTags.TryGetValue(program, out Dictionary<string, string?>? local) && local.TryGetValue(name, out type))
        {
            return true;
        }

        return _controllerTags.TryGetValue(name, out type);
    }

    private Dictionary<string, string?> Tags(string program)
    {
        if (!_programTags.TryGetValue(program, out Dictionary<string, string?>? tags))
        {
            tags = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            _programTags[program] = tags;
        }

        return tags;
    }

    private HashSet<string> Routines(string program)
    {
        if (!_routines.TryGetValue(program, out HashSet<string>? names))
        {
            names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _routines[program] = names;
        }

        return names;
    }
}
