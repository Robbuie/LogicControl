using System.Text.RegularExpressions;
using LogicControl.Core.Logic;
using LogicControl.Core.Model;

namespace LogicControl.Core.Analysis;

/// <summary>
/// Every place every tag is read or written, and every instruction in the project.
///
/// <para><b>Scope is resolved the way the controller resolves it.</b> A name in a program's
/// routine means that program's tag if it has one, otherwise the controller's; a name inside an
/// AOI means its own parameter or local tag. Logix names are case-insensitive, so lookups are
/// too. A name that resolves to nothing is kept in <see cref="Unresolved"/> rather than dropped -
/// in a whole-controller export that is a real problem; in a single-routine export it is
/// expected, and the findings say which.</para>
///
/// <para>Module tags (<c>Local:3:I</c>, <c>Rack1:O</c>) are not declared in the Tags section of
/// an export - the I/O tree creates them - so they are keyed by their own name and also counted
/// per module in <see cref="ModuleTagUses"/>.</para>
/// </summary>
public sealed partial class CrossReference
{
    private readonly Dictionary<string, List<TagUse>> _uses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _moduleUses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<TagUse>> _unresolved = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<InstructionSite> _sites = [];

    private CrossReference()
    {
    }

    /// <summary>Every instruction in every readable routine, ladder and ST, in project order.</summary>
    public IReadOnlyList<InstructionSite> Instructions => _sites;

    /// <summary>Uses keyed by <see cref="TagInfo.QualifiedName"/> (or the module tag's own name).</summary>
    public IReadOnlyDictionary<string, List<TagUse>> Uses => _uses;

    /// <summary>Names used in logic that match no declared tag, with where they were used.</summary>
    public IReadOnlyDictionary<string, List<TagUse>> Unresolved => _unresolved;

    /// <summary>How many times logic touches each module's tags, by module name.</summary>
    public IReadOnlyDictionary<string, int> ModuleTagUses => _moduleUses;

    /// <summary>Where <paramref name="tag"/> is used; empty when nowhere.</summary>
    public IReadOnlyList<TagUse> UsesOf(TagInfo tag) =>
        _uses.TryGetValue(tag.QualifiedName, out List<TagUse>? list) ? list : [];

    /// <summary>Routine names that a JSR (ladder or ST) calls, as Owner/Name.</summary>
    public IReadOnlySet<string> CalledRoutines { get; private set; } = new HashSet<string>();

    public static CrossReference Build(PlcProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var xref = new CrossReference();
        var scopes = new ScopeTable(project);
        var called = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, AoiInfo> aois = project.AddOnInstructions
            .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // Tags that alias another tag count as a use of what they point at - that is how a module
        // input reaches logic in most projects, and without it every aliased card looks unused.
        foreach (TagInfo alias in project.AllTags.Where(t => t.Kind == TagKind.Alias && t.AliasFor is not null))
        {
            foreach (string target in TagReference.BaseNames(alias.AliasFor))
            {
                var use = new TagUse("(alias)", 0, "Alias", alias.QualifiedName, TagAccess.Read, IsStructuredText: false);
                xref.Record(scopes, alias.Scope, ownerIsAoi: false, target, use);
            }
        }

        // A message writes its local element when it completes (a read) - that is how most
        // data from another controller lands in a tag - so a fired MSG counts as a write of it.
        Dictionary<string, TagInfo> messages = project.AllTags
            .Where(t => t.IsMessage && t.Message?.LocalElement is not null)
            .GroupBy(t => t.QualifiedName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (RoutineInfo routine in project.AllRoutines.Where(r => !r.IsProtected))
        {
            if (routine.Language == RoutineLanguage.Ladder)
            {
                foreach (RungInfo rung in routine.Rungs)
                {
                    foreach (Instruction instruction in RungParser.Parse(rung.Text))
                    {
                        xref._sites.Add(new InstructionSite(routine, rung.Number, instruction));
                        xref.RecordInstruction(scopes, aois, routine, rung.Number, instruction);
                        xref.RecordMessageData(scopes, messages, routine, rung.Number, instruction, isStructuredText: false);

                        if (instruction.Mnemonic.Equals("JSR", StringComparison.OrdinalIgnoreCase)
                            && instruction.Operands.Count > 0)
                        {
                            called.Add($"{routine.Owner}/{instruction.Operands[0]}");
                        }
                    }
                }
            }
            else if (routine.Language == RoutineLanguage.StructuredText)
            {
                xref.RecordStructuredText(scopes, routine, called, messages);
            }
        }

        xref.CalledRoutines = called;
        return xref;
    }

    private void RecordInstruction(
        ScopeTable scopes, Dictionary<string, AoiInfo> aois, RoutineInfo routine, int rung, Instruction instruction)
    {
        AoiInfo? called = null;
        bool isAoiCall = !InstructionCatalog.IsKnown(instruction.Mnemonic)
            && aois.TryGetValue(instruction.Mnemonic, out called);

        for (int i = 0; i < instruction.Operands.Count; i++)
        {
            string operand = instruction.Operands[i];
            OperandRole role = isAoiCall && called is not null
                ? AoiOperandRole(called, i)
                : InstructionCatalog.RoleOf(instruction.Mnemonic, i);

            if (role == OperandRole.NotATag)
            {
                continue;
            }

            IReadOnlyList<string> names = TagReference.BaseNames(operand);
            for (int n = 0; n < names.Count; n++)
            {
                // Only the operand's own tag is written. In MOV(Src, Arr[Idx]) the index is read.
                TagAccess access = role == OperandRole.Write && n == 0 ? TagAccess.Write : TagAccess.Read;
                var use = new TagUse(routine.QualifiedName, rung, instruction.Mnemonic, operand, access, IsStructuredText: false);
                Record(scopes, routine.Owner, routine.OwnerIsAoi, names[n], use);
            }
        }
    }

    /// <summary>
    /// For a MSG instruction, the message's local element: written for a read, read for a write.
    /// The element is named in the MESSAGE tag's configuration, not on the rung, so without this
    /// a tag filled by a message looks as if nothing ever writes it.
    /// </summary>
    private void RecordMessageData(
        ScopeTable scopes, Dictionary<string, TagInfo> messages, RoutineInfo routine, int location,
        Instruction instruction, bool isStructuredText)
    {
        if (!instruction.Mnemonic.Equals("MSG", StringComparison.OrdinalIgnoreCase) || instruction.Operands.Count == 0)
        {
            return;
        }

        string? baseName = TagReference.BaseNames(instruction.Operands[0]).FirstOrDefault();
        string? key = baseName is null ? null : scopes.Resolve(routine.Owner, routine.OwnerIsAoi, baseName);
        if (key is null || !messages.TryGetValue(key, out TagInfo? tag) || tag.Message?.LocalElement is not { } local)
        {
            return;
        }

        bool isWrite = tag.Message.MessageType?.Contains("Write", StringComparison.OrdinalIgnoreCase) ?? false;
        TagAccess access = isWrite ? TagAccess.Read : TagAccess.Write;

        IReadOnlyList<string> names = TagReference.BaseNames(local);
        for (int n = 0; n < names.Count; n++)
        {
            var use = new TagUse(routine.QualifiedName, location, $"MSG {tag.Name}", local,
                n == 0 ? access : TagAccess.Read, isStructuredText);
            Record(scopes, routine.Owner, routine.OwnerIsAoi, names[n], use);
        }
    }

    /// <summary>
    /// An AOI call passes its instance tag first, then each Required parameter in order. The
    /// instance is written (the AOI updates its own backing tag); an Output parameter is written,
    /// an InOut is passed by reference and may be either, and is counted as written.
    /// </summary>
    private static OperandRole AoiOperandRole(AoiInfo aoi, int index)
    {
        if (index == 0)
        {
            return OperandRole.Write;
        }

        int p = index - 1;
        if (p >= aoi.CallParameters.Count)
        {
            return OperandRole.Read;
        }

        return aoi.CallParameters[p].Usage is "Output" or "InOut" ? OperandRole.Write : OperandRole.Read;
    }

    private void RecordStructuredText(
        ScopeTable scopes, RoutineInfo routine, HashSet<string> called, Dictionary<string, TagInfo> messages)
    {
        bool inBlockComment = false;

        for (int lineNumber = 0; lineNumber < routine.StructuredText.Count; lineNumber++)
        {
            string line = StripComments(routine.StructuredText[lineNumber], ref inBlockComment);
            if (line.Trim().Length == 0)
            {
                continue;
            }

            foreach (Match call in JsrCall().Matches(line))
            {
                called.Add($"{routine.Owner}/{call.Groups[1].Value}");
            }

            // Instruction calls written in ST - MSG(Msg1); TONR(T1); - are sites too, so the
            // communications map finds a message fired from ST as well as from ladder.
            foreach (Match call in StCall().Matches(line))
            {
                string mnemonic = call.Groups[1].Value;
                int open = call.Index + call.Length - 1;
                int close = line.IndexOf(')', open);
                string inner = close > open ? line[(open + 1)..close] : string.Empty;
                var instruction = new Instruction(mnemonic, RungParser.SplitOperands(inner), call.Index);
                _sites.Add(new InstructionSite(routine, lineNumber, instruction));
                RecordMessageData(scopes, messages, routine, lineNumber, instruction, isStructuredText: true);
            }

            int assign = line.IndexOf(":=", StringComparison.Ordinal);
            string left = assign >= 0 ? line[..assign] : string.Empty;
            string right = assign >= 0 ? line[(assign + 2)..] : line;

            foreach (string name in TagReference.BaseNames(left).Take(1))
            {
                Record(scopes, routine.Owner, routine.OwnerIsAoi, name,
                    new TagUse(routine.QualifiedName, lineNumber, ":=", left.Trim(), TagAccess.Write, IsStructuredText: true));
            }

            // Anything else on the left (an index) and everything on the right is read.
            foreach (string name in TagReference.BaseNames(left).Skip(1).Concat(TagReference.BaseNames(right)))
            {
                Record(scopes, routine.Owner, routine.OwnerIsAoi, name,
                    new TagUse(routine.QualifiedName, lineNumber, "ST", line.Trim(), TagAccess.Read, IsStructuredText: true));
            }
        }
    }

    private static string StripComments(string line, ref bool inBlock)
    {
        var kept = new System.Text.StringBuilder(line.Length);
        for (int i = 0; i < line.Length; i++)
        {
            if (inBlock)
            {
                if (i + 1 < line.Length && ((line[i] == '*' && line[i + 1] == ')') || (line[i] == '*' && line[i + 1] == '/')))
                {
                    inBlock = false;
                    i++;
                }

                continue;
            }

            if (i + 1 < line.Length && line[i] == '/' && line[i + 1] == '/')
            {
                break;
            }

            if (i + 1 < line.Length && ((line[i] == '(' && line[i + 1] == '*') || (line[i] == '/' && line[i + 1] == '*')))
            {
                inBlock = true;
                i++;
                continue;
            }

            kept.Append(line[i]);
        }

        return kept.ToString();
    }

    private void Record(ScopeTable scopes, string? owner, bool ownerIsAoi, string baseName, TagUse use)
    {
        string? module = TagReference.ModuleOf(baseName);
        if (module is not null)
        {
            Add(_uses, baseName, use);
            _moduleUses[module] = _moduleUses.TryGetValue(module, out int count) ? count + 1 : 1;
            return;
        }

        string? key = scopes.Resolve(owner, ownerIsAoi, baseName);
        Add(key is null ? _unresolved : _uses, key ?? baseName, use);
    }

    private static void Add(Dictionary<string, List<TagUse>> map, string key, TagUse use)
    {
        if (!map.TryGetValue(key, out List<TagUse>? list))
        {
            list = [];
            map[key] = list;
        }

        list.Add(use);
    }

    [GeneratedRegex(@"\bJSR\s*\(\s*([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.IgnoreCase)]
    private static partial Regex JsrCall();

    [GeneratedRegex(@"\b(MSG|GSV|SSV|IOT|JSR)\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex StCall();

    /// <summary>Which names are declared where, for resolving a bare name to a qualified one.</summary>
    private sealed class ScopeTable
    {
        private readonly HashSet<string> _controller;
        private readonly Dictionary<string, HashSet<string>> _programs = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> _aois = new(StringComparer.OrdinalIgnoreCase);

        public ScopeTable(PlcProject project)
        {
            _controller = new HashSet<string>(project.Tags.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);

            foreach (ProgramInfo program in project.Programs)
            {
                _programs[program.Name] = new HashSet<string>(program.Tags.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
            }

            foreach (AoiInfo aoi in project.AddOnInstructions)
            {
                _aois[aoi.Name] = new HashSet<string>(
                    aoi.Parameters.Concat(aoi.LocalTags).Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>The qualified name the controller would bind <paramref name="name"/> to, or null.</summary>
        public string? Resolve(string? owner, bool ownerIsAoi, string name)
        {
            Dictionary<string, HashSet<string>> locals = ownerIsAoi ? _aois : _programs;
            if (owner is not null && locals.TryGetValue(owner, out HashSet<string>? local) && local.Contains(name))
            {
                return $"{owner}.{name}";
            }

            // Inside an AOI only its own parameters and locals exist - no controller scope.
            if (ownerIsAoi)
            {
                return null;
            }

            return _controller.Contains(name) ? name : null;
        }
    }
}
