using System.Globalization;
using System.Text.RegularExpressions;
using LogicControl.Core.Logic;
using LogicControl.Core.Model;

namespace LogicControl.Core.Authoring;

/// <summary>
/// Says what is wrong with drafts before Studio 5000 does.
///
/// <para>An <see cref="DraftSeverity.Error"/> is something the import would refuse or the
/// verify would fail on - an illegal name, a rung with an unclosed branch, a TON with two
/// operands, a member of an unknown type. Exports are blocked while any remain. A
/// <see cref="DraftSeverity.Warning"/> is something worth a look that might be right: a tag the
/// project does not declare (it may exist in the project the file is imported into), an
/// instruction outside this tool's table, a <c>?</c> placeholder.</para>
///
/// <para>Same direction as the findings rules: when this tool does not know, it warns rather than
/// blocks, because Studio 5000's own verify is the last word and an export nobody can make is
/// worse than one Studio 5000 flags.</para>
/// </summary>
public static partial class DraftChecker
{
    public static IReadOnlyList<DraftIssue> Check(DevelopmentSet set, PlcProject? project = null)
    {
        ArgumentNullException.ThrowIfNull(set);
        var context = new DraftContext(set, project);
        var issues = new List<DraftIssue>();

        CheckDuplicates(set.DataTypes, d => d.Name, "data type", issues);
        CheckDuplicates(set.AddOnInstructions, a => a.Name, "Add-On Instruction", issues);
        CheckDuplicates(set.Tags, t => t.QualifiedName, "tag", issues);
        CheckDuplicates(set.Programs, p => p.Name, "program", issues);
        CheckDuplicates(set.Routines, r => r.QualifiedName, "routine", issues);

        foreach (UdtDraft udt in set.DataTypes)
        {
            CheckUdt(udt, context, issues);
        }

        foreach (AoiDraft aoi in set.AddOnInstructions)
        {
            CheckAoi(aoi, context, issues);
        }

        foreach (TagDraft tag in set.Tags)
        {
            CheckTag(tag, context, issues);
        }

        foreach (ProgramDraft program in set.Programs)
        {
            string subject = $"Program {program.Name}";
            Name(program.Name, subject, program, issues);
            if (program.MainRoutineName is { Length: > 0 } main && !context.RoutineExists(program.Name, main))
            {
                issues.Add(DraftIssue.Error(subject, $"Main routine '{main}' is not a routine in this program.", program));
            }
        }

        foreach (RoutineDraft routine in set.Routines)
        {
            string subject = $"Routine {routine.QualifiedName}";
            Name(routine.Name, subject, routine, issues);
            if (!context.ProgramExists(routine.Program))
            {
                issues.Add(DraftIssue.Error(subject,
                    $"Program '{routine.Program}' is not in the open project or the drafts. Add it as a program draft, or pick an existing one.",
                    routine));
            }

            var scope = RungScope.ForProgram(context, routine.Program);
            for (int i = 0; i < routine.Rungs.Count; i++)
            {
                foreach (DraftIssue issue in CheckRung(routine.Rungs[i].Text, scope))
                {
                    issues.Add(issue with { Subject = subject, Target = routine, Rung = i });
                }
            }
        }

        return issues;
    }

    /// <summary>
    /// Checks one rung against what <paramref name="scope"/> can see. Used live by the rung editor,
    /// so it never throws and is cheap enough to run on every keystroke.
    /// </summary>
    public static IReadOnlyList<DraftIssue> CheckRung(string? text, RungScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var issues = new List<DraftIssue>();
        const string subject = "Rung";

        LadderRung rung = LadderParser.Parse(text);
        foreach (string problem in rung.Problems)
        {
            issues.Add(DraftIssue.Error(subject, problem));
        }

        List<Instruction> instructions = rung.Instructions.ToList();
        if (instructions.Count == 0)
        {
            issues.Add(DraftIssue.Warning(subject, "The rung is empty. Studio 5000 will not verify an empty rung."));
            return issues;
        }

        foreach (Instruction instruction in instructions)
        {
            InstructionShape? shape = scope.ShapeOf(instruction.Mnemonic);
            string at = $"{instruction.Mnemonic} at position {instruction.Position + 1}";

            if (shape is null)
            {
                issues.Add(DraftIssue.Warning(subject, InstructionCatalog.IsKnown(instruction.Mnemonic)
                    ? $"{at}: operands are not checked for this instruction."
                    : $"{at}: not in LogicControl's instruction table and not an AOI here. Studio 5000 will say whether it exists."));
            }
            else
            {
                int count = instruction.Operands.Count;
                if (count < shape.MinOperands || (shape.MaxOperands is int max && count > max))
                {
                    string expected = shape.MaxOperands is null ? $"at least {shape.MinOperands}" : shape.MinOperands.ToString(CultureInfo.InvariantCulture);
                    issues.Add(DraftIssue.Error(subject,
                        $"{at} takes {expected} operand{(shape.MinOperands == 1 ? string.Empty : "s")} ({string.Join(", ", shape.OperandNames)}); it has {count}."));
                }
            }

            for (int i = 0; i < instruction.Operands.Count; i++)
            {
                string operand = instruction.Operands[i];
                string name = shape?.NameOf(i) ?? $"operand {i + 1}";

                if (operand.Length == 0)
                {
                    issues.Add(DraftIssue.Error(subject, $"{at}: {name} is empty."));
                    continue;
                }

                // Studio 5000 itself exports a timer's preset and accumulator - and a file
                // instruction's length and position - as '?': the value lives in the tag.
                if (operand == "?" && shape is not null && ValueInTag.Contains(name))
                {
                    continue;
                }

                if (operand == "?")
                {
                    issues.Add(DraftIssue.Warning(subject, $"{at}: {name} is still '?'. The rung imports but will not verify."));
                    continue;
                }

                OperandRole role = InstructionCatalog.RoleOf(instruction.Mnemonic, i);

                if (string.Equals(instruction.Mnemonic, "JSR", StringComparison.OrdinalIgnoreCase) && i == 0)
                {
                    if (!scope.RoutineExists(operand))
                    {
                        issues.Add(DraftIssue.Warning(subject, $"{at}: routine '{operand}' is not in this program or the drafts."));
                    }

                    continue;
                }

                if (role == OperandRole.NotATag)
                {
                    continue;
                }

                CheckOperandTags(operand, at, scope, issues);
            }
        }

        return issues;
    }

    private static void CheckOperandTags(string operand, string at, RungScope scope, List<DraftIssue> issues)
    {
        foreach (string baseName in TagReference.BaseNames(operand).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (baseName.Contains(':', StringComparison.Ordinal))
            {
                // Module tags - Local:1:I - belong to the I/O tree, which drafts do not write.
                continue;
            }

            if (!scope.TryFindTag(baseName, out string? type))
            {
                issues.Add(DraftIssue.Warning("Rung", $"{at}: tag '{baseName}' is not declared {scope.Where}."));
                continue;
            }

            // One level of member check: Tag.Member or Tag[i].Member, the common case and the one
            // that catches a mistyped .DN or a renamed UDT member.
            Match m = MemberPath().Match(operand.Trim());
            if (!m.Success || !string.Equals(m.Groups["base"].Value, baseName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string member = m.Groups["member"].Value;
            if (int.TryParse(member, NumberStyles.None, CultureInfo.InvariantCulture, out int bit))
            {
                if (LogixTypes.BitsOf(type) is int bits && bit >= bits)
                {
                    issues.Add(DraftIssue.Error("Rung", $"{at}: '{operand}' - {type} has bits 0 to {bits - 1}."));
                }

                continue;
            }

            IEnumerable<string>? members = scope.MembersOf(type);
            if (members is not null && !members.Contains(member, StringComparer.OrdinalIgnoreCase))
            {
                issues.Add(DraftIssue.Error("Rung", $"{at}: '{operand}' - {type} has no member '{member}'."));
            }
        }
    }

    private static void CheckUdt(UdtDraft udt, DraftContext context, List<DraftIssue> issues)
    {
        string subject = $"Data type {udt.Name}";
        Name(udt.Name, subject, udt, issues);

        if (LogixTypes.IsBuiltIn(udt.Name))
        {
            issues.Add(DraftIssue.Error(subject, $"'{udt.Name}' is a built-in type name.", udt));
        }

        if (udt.Members.Count == 0)
        {
            issues.Add(DraftIssue.Error(subject, "A data type needs at least one member.", udt));
        }

        CheckDuplicates(udt.Members, m => m.Name, "member", issues, subject, udt);

        foreach (MemberDraft member in udt.Members)
        {
            Name(member.Name, subject, udt, issues, "Member name");

            if (!context.TypeExists(member.DataType))
            {
                issues.Add(DraftIssue.Error(subject, $"Member '{member.Name}': type '{member.DataType}' is not a built-in type, a UDT or an AOI here.", udt));
            }
            else if (string.Equals(member.DataType, udt.Name, StringComparison.OrdinalIgnoreCase) || Contains(context, member.DataType, udt.Name, depth: 0))
            {
                issues.Add(DraftIssue.Error(subject, $"Member '{member.Name}' makes the type contain itself.", udt));
            }

            if (member.Dimension < 0)
            {
                issues.Add(DraftIssue.Error(subject, $"Member '{member.Name}' has a negative dimension.", udt));
            }
            else if (LogixTypes.IsBool(member.DataType) && member.Dimension > 0 && member.Dimension % 32 != 0)
            {
                issues.Add(DraftIssue.Error(subject, $"Member '{member.Name}': a BOOL array in a UDT must be a multiple of 32 long (BOOL[32], BOOL[64]...).", udt));
            }
        }
    }

    private static bool Contains(DraftContext context, string type, string target, int depth)
    {
        if (depth > 16 || context.UdtMembers(type) is not { } members)
        {
            return false;
        }

        return members.Values.Any(t => string.Equals(t, target, StringComparison.OrdinalIgnoreCase) || Contains(context, t, target, depth + 1));
    }

    private static void CheckAoi(AoiDraft aoi, DraftContext context, List<DraftIssue> issues)
    {
        string subject = $"Add-On {aoi.Name}";
        Name(aoi.Name, subject, aoi, issues);

        if (InstructionSignatures.Find(aoi.Name) is not null || InstructionCatalog.IsKnown(aoi.Name) || LogixTypes.IsBuiltIn(aoi.Name))
        {
            issues.Add(DraftIssue.Error(subject, $"'{aoi.Name}' is a built-in instruction or type name.", aoi));
        }

        CheckDuplicates(
            aoi.Parameters.Select(p => p.Name).Concat(aoi.LocalTags.Select(l => l.Name)).ToList(),
            n => n, "parameter or local tag", issues, subject, aoi);

        foreach (AoiParameterDraft p in aoi.Parameters)
        {
            Name(p.Name, subject, aoi, issues, "Parameter name");

            if (p.Name.Equals("EnableIn", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("EnableOut", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(DraftIssue.Error(subject, $"'{p.Name}' is created automatically; remove it from the parameter list.", aoi));
            }

            if (p.Usage is not ("Input" or "Output" or "InOut"))
            {
                issues.Add(DraftIssue.Error(subject, $"Parameter '{p.Name}': usage must be Input, Output or InOut.", aoi));
            }

            if (!context.TypeExists(p.DataType))
            {
                issues.Add(DraftIssue.Error(subject, $"Parameter '{p.Name}': type '{p.DataType}' is not known.", aoi));
            }
            else if (p.Usage is "Input" or "Output" && !LogixTypes.IsAtomic(p.DataType))
            {
                issues.Add(DraftIssue.Error(subject, $"Parameter '{p.Name}': an {p.Usage} parameter must be an atomic type; pass a {p.DataType} as InOut.", aoi));
            }

            if (string.Equals(p.DataType, aoi.Name, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(DraftIssue.Error(subject, $"Parameter '{p.Name}' is of the AOI's own type.", aoi));
            }
        }

        foreach (AoiLocalTagDraft local in aoi.LocalTags)
        {
            Name(local.Name, subject, aoi, issues, "Local tag name");
            if (!context.TypeExists(local.DataType))
            {
                issues.Add(DraftIssue.Error(subject, $"Local tag '{local.Name}': type '{local.DataType}' is not known.", aoi));
            }
        }

        if (aoi.Logic.Count == 0)
        {
            issues.Add(DraftIssue.Warning(subject, "The Logic routine has no rungs.", aoi));
        }

        var scope = RungScope.ForAoi(context, aoi);
        for (int i = 0; i < aoi.Logic.Count; i++)
        {
            foreach (DraftIssue issue in CheckRung(aoi.Logic[i].Text, scope))
            {
                issues.Add(issue with { Subject = subject, Target = aoi, Rung = i });
            }
        }
    }

    private static void CheckTag(TagDraft tag, DraftContext context, List<DraftIssue> issues)
    {
        string subject = $"Tag {tag.QualifiedName}";
        Name(tag.Name, subject, tag, issues);

        if (tag.Program is not null && !context.ProgramExists(tag.Program))
        {
            issues.Add(DraftIssue.Error(subject, $"Program '{tag.Program}' is not in the open project or the drafts.", tag));
        }

        if (tag.AliasFor is not null)
        {
            if (string.IsNullOrWhiteSpace(tag.AliasFor))
            {
                issues.Add(DraftIssue.Error(subject, "An alias needs something to point at.", tag));
            }

            return;
        }

        if (!context.TypeExists(tag.DataType))
        {
            issues.Add(DraftIssue.Error(subject, $"Type '{tag.DataType}' is not a built-in type, a UDT or an AOI here.", tag));
        }

        if (tag.Dimensions is { Length: > 0 } dims)
        {
            string[] parts = dims.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 3 || parts.Any(p => !int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n <= 0))
            {
                issues.Add(DraftIssue.Error(subject, $"Dimensions '{dims}' should be one to three positive numbers, e.g. \"10\" or \"4 8\".", tag));
            }
        }

        if (context.Project is { } project)
        {
            bool exists = tag.Program is null
                ? project.Tags.Any(t => string.Equals(t.Name, tag.Name, StringComparison.OrdinalIgnoreCase))
                : project.Programs.Any(p => string.Equals(p.Name, tag.Program, StringComparison.OrdinalIgnoreCase)
                    && p.Tags.Any(t => string.Equals(t.Name, tag.Name, StringComparison.OrdinalIgnoreCase)));

            if (exists)
            {
                issues.Add(DraftIssue.Warning(subject, "The open project already has a tag of this name; importing will ask whether to overwrite it.", tag));
            }
        }
    }

    private static void Name(string name, string subject, object target, List<DraftIssue> issues, string what = "Name")
    {
        if (LogixTypes.NameProblem(name, what) is { } problem)
        {
            issues.Add(DraftIssue.Error(subject, problem, target));
        }
    }

    private static void CheckDuplicates<T>(
        IEnumerable<T> items, Func<T, string> key, string what, List<DraftIssue> issues, string? subject = null, object? target = null)
    {
        foreach (IGrouping<string, T> group in items.GroupBy(key, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1 && g.Key.Length > 0))
        {
            issues.Add(DraftIssue.Error(subject ?? $"{char.ToUpperInvariant(what[0])}{what[1..]} {group.Key}",
                $"There are {group.Count()} {what}s named '{group.Key}'. Logix names ignore case.", target ?? group.First()));
        }
    }

    private static readonly HashSet<string> ValueInTag = new(StringComparer.Ordinal) { "Preset", "Accum", "Length", "Position" };

    [GeneratedRegex(@"^(?<base>[A-Za-z_][A-Za-z0-9_]*)(\[[^\]]*\])?\.(?<member>[A-Za-z0-9_]+)")]
    private static partial Regex MemberPath();
}

public enum DraftSeverity
{
    Warning,
    Error,
}

/// <summary>One thing wrong with a draft. <see cref="Rung"/> is the zero-based rung index when it is about a rung.</summary>
public sealed record DraftIssue(DraftSeverity Severity, string Subject, string Message, object? Target = null, int? Rung = null)
{
    public static DraftIssue Error(string subject, string message, object? target = null) => new(DraftSeverity.Error, subject, message, target);

    public static DraftIssue Warning(string subject, string message, object? target = null) => new(DraftSeverity.Warning, subject, message, target);

    public bool IsError => Severity == DraftSeverity.Error;

    public string Location => Rung is int r ? $"{Subject}, rung {r}" : Subject;
}

/// <summary>
/// What a rung can see: a program's tags plus controller tags, or an AOI's parameters and local
/// tags and nothing else - AOI logic cannot reach outside the instruction.
/// </summary>
public sealed class RungScope
{
    private readonly Func<string, (bool Found, string? Type)> _find;
    private readonly Func<string, bool> _routineExists;
    private readonly DraftContext _context;

    private RungScope(DraftContext context, string where, Func<string, (bool, string?)> find, Func<string, bool> routineExists)
    {
        _context = context;
        Where = where;
        _find = find;
        _routineExists = routineExists;
    }

    /// <summary>"in program Line2 or at controller scope" - for messages.</summary>
    public string Where { get; }

    public static RungScope ForProgram(DraftContext context, string program)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new RungScope(
            context,
            $"in program {program} or at controller scope",
            name => context.TryFindTag(program, name, out string? type) ? (true, type) : (false, null),
            routine => context.RoutineExists(program, routine));
    }

    public static RungScope ForAoi(DraftContext context, AoiDraft aoi)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(aoi);

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["EnableIn"] = "BOOL",
            ["EnableOut"] = "BOOL",
        };

        foreach (AoiParameterDraft p in aoi.Parameters)
        {
            names[p.Name] = p.DataType;
        }

        foreach (AoiLocalTagDraft l in aoi.LocalTags)
        {
            names[l.Name] = l.DataType;
        }

        return new RungScope(
            context,
            $"as a parameter or local tag of {aoi.Name}",
            name => names.TryGetValue(name, out string? type) ? (true, type) : (false, null),
            _ => false);
    }

    public InstructionShape? ShapeOf(string mnemonic) => _context.ShapeOf(mnemonic);

    public bool TryFindTag(string name, out string? type)
    {
        (bool found, string? t) = _find(name);
        type = t;
        return found;
    }

    public bool RoutineExists(string name) => _routineExists(name);

    public IEnumerable<string>? MembersOf(string? type) => _context.MembersOf(type);
}
