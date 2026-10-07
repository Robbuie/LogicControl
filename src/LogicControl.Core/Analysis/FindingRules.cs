using System.Globalization;
using LogicControl.Core.Logic;
using LogicControl.Core.Model;

namespace LogicControl.Core.Analysis;

/// <summary>
/// The checks. Each rule is one small method with a stable id, and each says plainly what it
/// found and why it matters - written for the controls engineer reading the list, not for a log.
///
/// <para><b>Severity is about consequence, not certainty.</b> Error: the project will not do what
/// it looks like it does (two modules on one IP, a consumed tag from nobody). Warning: probably a
/// mistake (a double coil, a message nothing fires). Info: worth knowing (unused tags, keying
/// disabled). The rules lean towards under-reporting: a findings list people learn to ignore is
/// worse than a shorter one.</para>
///
/// <para>Rules that only make sense for a whole controller - unscheduled programs, unresolved
/// tags, uncalled routines - are skipped for a partial export, where those are expected.</para>
/// </summary>
public static class FindingRules
{
    /// <summary>Below this an RPI is unusually aggressive for ordinary I/O.</summary>
    private const double FastRpiMs = 2.0;

    public static IReadOnlyList<Finding> Run(
        PlcProject project, CrossReference xref, IReadOnlyList<HardwareNode> hardware, IReadOnlyList<CommLink> comms)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(xref);
        ArgumentNullException.ThrowIfNull(hardware);
        ArgumentNullException.ThrowIfNull(comms);

        bool whole = string.Equals(project.TargetType, "Controller", StringComparison.OrdinalIgnoreCase);
        var findings = new List<Finding>();

        DuplicateIps(project, findings);
        InhibitedModules(project, findings);
        KeyingDisabled(project, findings);
        FastRpis(project, findings);
        ModulesWithUnusedData(project, xref, findings);
        UnmonitoredEthernetModules(project, comms, findings);
        ConsumedFromUnknownProducer(project, findings);
        Messages(project, xref, findings);
        DoubleCoils(xref, findings);
        UnusedTags(project, xref, findings);
        ProtectedContent(project, findings);
        DisabledAndInhibited(project, findings);
        RpiSlowerThanTask(project, xref, findings);
        StatusReadNotUsed(xref, findings);
        MessagesWithoutResultHandling(project, xref, findings);
        WrittenFromTwoTasks(project, xref, findings);

        if (whole)
        {
            UnscheduledPrograms(project, findings);
            UncalledRoutines(project, xref, findings);
            UnresolvedNames(xref, findings);
            UnusedDefinitions(project, xref, findings);
        }

        return findings
            .OrderBy(f => f.Severity)
            .ThenBy(f => f.Category, StringComparer.Ordinal)
            .ThenBy(f => f.Subject, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ------------------------------------------------------------------ Hardware

    private static void DuplicateIps(PlcProject project, List<Finding> findings)
    {
        foreach (IGrouping<string, ModuleInfo> group in project.Modules
            .Where(m => m.IpAddress is not null && !m.Inhibited)
            .GroupBy(m => m.IpAddress!, StringComparer.Ordinal)
            .Where(g => g.Count() > 1))
        {
            findings.Add(new Finding(
                FindingSeverity.Error, "LC-HW-001", "Hardware", group.Key,
                $"{group.Count()} modules are configured at {group.Key}: {string.Join(", ", group.Select(m => m.Name))}. "
                + "Only one can own the address; the others will never connect."));
        }
    }

    private static void InhibitedModules(PlcProject project, List<Finding> findings)
    {
        foreach (ModuleInfo module in project.Modules.Where(m => m.Inhibited))
        {
            findings.Add(new Finding(
                FindingSeverity.Warning, "LC-HW-002", "Hardware", module.Name,
                $"{module.Name} ({module.CatalogNumber}) is inhibited. The controller does not connect to it, "
                + "so its inputs hold their last values and its outputs are not driven."));
        }
    }

    private static void KeyingDisabled(PlcProject project, List<Finding> findings)
    {
        foreach (ModuleInfo module in project.Modules.Where(m =>
            !m.IsLocal && string.Equals(m.Keying, "Disabled", StringComparison.OrdinalIgnoreCase)))
        {
            findings.Add(new Finding(
                FindingSeverity.Info, "LC-HW-003", "Hardware", module.Name,
                $"Electronic keying is disabled on {module.Name}. Any module that answers at this address "
                + "will be accepted, including the wrong catalog number."));
        }
    }

    private static void FastRpis(PlcProject project, List<Finding> findings)
    {
        foreach (ModuleInfo module in project.Modules)
        {
            double? fastest = module.Connections.Min(c => c.RpiMs);
            if (fastest is { } rpi && rpi < FastRpiMs)
            {
                findings.Add(new Finding(
                    FindingSeverity.Info, "LC-HW-004", "Hardware", module.Name,
                    $"{module.Name} is scheduled every {rpi.ToString("0.###", CultureInfo.InvariantCulture)} ms. "
                    + "That is fast for ordinary I/O and adds to the adapter's packet load - worth confirming it is needed."));
            }
        }
    }

    private static void ModulesWithUnusedData(PlcProject project, CrossReference xref, List<Finding> findings)
    {
        Dictionary<string, ModuleInfo> byName = project.Modules
            .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (ModuleInfo module in project.Modules.Where(m =>
            !m.IsLocal && !m.Inhibited && m.Connections.Any(c => (c.InputSize ?? 0) + (c.OutputSize ?? 0) > 0)))
        {
            string prefix = ModuleTags.PrefixOf(module, byName);
            if (!xref.ModuleTagUses.ContainsKey(prefix))
            {
                findings.Add(new Finding(
                    FindingSeverity.Info, "LC-HW-005", "Hardware", module.Name,
                    $"{module.Name} exchanges data but no logic or alias touches its {prefix}:I / {prefix}:O tags. "
                    + "Either it is spare, or its data is used somewhere this export does not include."));
            }
        }
    }

    private static void UnmonitoredEthernetModules(PlcProject project, IReadOnlyList<CommLink> comms, List<Finding> findings)
    {
        var watched = new HashSet<string>(
            comms.Where(c => c.Kind == CommKind.StatusRead).Select(c => c.From), StringComparer.OrdinalIgnoreCase);

        List<ModuleInfo> silent = project.Modules
            .Where(m => !m.IsLocal && !m.Inhibited && m.IpAddress is not null && m.Connections.Count > 0
                && !m.MajorFaultOnConnectionFailure && !watched.Contains(m.Name))
            .ToList();

        if (silent.Count > 0)
        {
            findings.Add(new Finding(
                FindingSeverity.Info, "LC-COM-001", "Communications", $"{silent.Count} Ethernet modules",
                "These modules neither fault the controller when their connection drops nor have a GSV "
                + $"reading their status, so a lost connection is silent: {string.Join(", ", silent.Select(m => m.Name))}."));
        }
    }

    private static void ConsumedFromUnknownProducer(PlcProject project, List<Finding> findings)
    {
        var modules = new HashSet<string>(project.Modules.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
        foreach (TagInfo tag in project.Tags.Where(t => t.Kind == TagKind.Consumed))
        {
            string? producer = tag.Consume?.Producer;
            if (producer is null || !modules.Contains(producer))
            {
                findings.Add(new Finding(
                    FindingSeverity.Error, "LC-COM-002", "Communications", tag.Name,
                    $"Consumed tag {tag.Name} names producer '{producer ?? "(none)"}', which is not in the I/O tree. "
                    + "The connection cannot be made."));
            }
        }
    }

    // ------------------------------------------------------------------ Messages

    private static void Messages(PlcProject project, CrossReference xref, List<Finding> findings)
    {
        var fired = new HashSet<string>(
            xref.Instructions
                .Where(s => s.Instruction.Mnemonic.Equals("MSG", StringComparison.OrdinalIgnoreCase) && s.Instruction.Operands.Count > 0)
                .SelectMany(s => TagReference.BaseNames(s.Instruction.Operands[0]).Take(1)),
            StringComparer.OrdinalIgnoreCase);

        var modules = new HashSet<string>(project.Modules.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);

        foreach (TagInfo tag in project.AllTags.Where(t => t.IsMessage))
        {
            if (!fired.Contains(tag.Name))
            {
                findings.Add(new Finding(
                    FindingSeverity.Warning, "LC-MSG-001", "Messages", tag.QualifiedName,
                    $"MESSAGE tag {tag.QualifiedName} is configured but no MSG instruction uses it."));
            }

            string? path = tag.Message?.ConnectionPath;
            if (path is null)
            {
                continue;
            }

            string first = path.Split(',')[0].Trim();
            bool numeric = first.Length > 0 && char.IsAsciiDigit(first[0]);
            if (!numeric && !modules.Contains(first))
            {
                findings.Add(new Finding(
                    FindingSeverity.Warning, "LC-MSG-002", "Messages", tag.QualifiedName,
                    $"The path of {tag.QualifiedName} starts at '{first}', which is not a module in the I/O tree. "
                    + "The message will error with a path fault."));
            }
        }
    }

    // ------------------------------------------------------------------ Logic

    private static void DoubleCoils(CrossReference xref, List<Finding> findings)
    {
        foreach (IGrouping<string, InstructionSite> group in xref.Instructions
            .Where(s => InstructionCatalog.IsCoil(s.Instruction.Mnemonic) && s.Instruction.Operands.Count > 0
                && !s.Routine.OwnerIsAoi)
            .GroupBy(s => $"{s.Routine.Owner}|{s.Instruction.Operands[0]}", StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1))
        {
            InstructionSite first = group.First();
            findings.Add(new Finding(
                FindingSeverity.Warning, "LC-LOG-001", "Logic", first.Instruction.Operands[0],
                $"OTE({first.Instruction.Operands[0]}) appears {group.Count()} times. The last rung scanned wins, "
                + "so the earlier ones have no effect on the output.",
                string.Join(", ", group.Select(s => $"{s.Routine.QualifiedName} {s.LocationText}")),
                group.Select(FindingSite.Of).ToList()));
        }
    }

    private static void UnusedTags(PlcProject project, CrossReference xref, List<Finding> findings)
    {
        List<TagInfo> unused = project.AllTags
            .Where(t => t.Kind is TagKind.Base or TagKind.Alias
                && !t.IsMessage
                && t.Usage is null) // program parameters are wired by connection, not referenced
            .Where(t => xref.UsesOf(t).Count == 0)
            .ToList();

        if (unused.Count == 0)
        {
            return;
        }

        // One finding listing them, not one per tag: a big project has hundreds, and a findings
        // list that is 90% "unused tag" hides the five things that matter.
        findings.Add(new Finding(
            FindingSeverity.Info, "LC-LOG-002", "Logic", $"{unused.Count} unused tags",
            "No logic in this export reads or writes these. They may be used by an HMI or another "
            + $"controller: {string.Join(", ", unused.Take(40).Select(t => t.QualifiedName))}"
            + (unused.Count > 40 ? $" and {unused.Count - 40} more." : ".")));
    }

    private static void ProtectedContent(PlcProject project, List<Finding> findings)
    {
        List<string> names = project.AddOnInstructions.Where(a => a.IsProtected).Select(a => $"AOI {a.Name}")
            .Concat(project.AllRoutines.Where(r => r.IsProtected).Select(r => r.QualifiedName))
            .ToList();

        if (names.Count > 0)
        {
            findings.Add(new Finding(
                FindingSeverity.Info, "LC-LOG-003", "Logic", $"{names.Count} protected",
                $"Source-protected, so their logic is not in the export and not analysed: {string.Join(", ", names)}."));
        }
    }

    private static void DisabledAndInhibited(PlcProject project, List<Finding> findings)
    {
        foreach (ProgramInfo program in project.Programs.Where(p => p.Disabled))
        {
            findings.Add(new Finding(
                FindingSeverity.Info, "LC-LOG-004", "Logic", program.Name, $"Program {program.Name} is disabled."));
        }

        foreach (TaskInfo task in project.Tasks.Where(t => t.InhibitTask))
        {
            findings.Add(new Finding(
                FindingSeverity.Warning, "LC-LOG-005", "Logic", task.Name,
                $"Task {task.Name} is inhibited - none of its {task.ScheduledPrograms.Count} programs execute."));
        }
    }

    private static void UnscheduledPrograms(PlcProject project, List<Finding> findings)
    {
        var scheduled = new HashSet<string>(project.Tasks.SelectMany(t => t.ScheduledPrograms), StringComparer.OrdinalIgnoreCase);
        foreach (ProgramInfo program in project.Programs.Where(p => !scheduled.Contains(p.Name)))
        {
            findings.Add(new Finding(
                FindingSeverity.Warning, "LC-LOG-006", "Logic", program.Name,
                $"Program {program.Name} is not scheduled in any task, so it never runs."));
        }
    }

    private static void UncalledRoutines(PlcProject project, CrossReference xref, List<Finding> findings)
    {
        foreach (ProgramInfo program in project.Programs)
        {
            foreach (RoutineInfo routine in program.Routines)
            {
                bool entry = string.Equals(routine.Name, program.MainRoutineName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(routine.Name, program.FaultRoutineName, StringComparison.OrdinalIgnoreCase);

                if (!entry && !xref.CalledRoutines.Contains(routine.QualifiedName))
                {
                    findings.Add(new Finding(
                        FindingSeverity.Warning, "LC-LOG-007", "Logic", routine.QualifiedName,
                        $"Routine {routine.Name} is not the main or fault routine and no JSR in {program.Name} calls it, so it never runs.",
                        Sites: [new FindingSite(routine.QualifiedName, null)]));
                }
            }
        }
    }

    private static void UnresolvedNames(CrossReference xref, List<Finding> findings)
    {
        foreach (KeyValuePair<string, List<TagUse>> pair in xref.Unresolved)
        {
            findings.Add(new Finding(
                FindingSeverity.Warning, "LC-LOG-008", "Logic", pair.Key,
                $"'{pair.Key}' is used in logic but is not declared at controller scope or in the program that uses it.",
                string.Join(", ", pair.Value.Take(5).Select(u => $"{u.Routine} {u.LocationText}")),
                pair.Value.Select(FindingSite.Of).ToList()));
        }
    }

    // ------------------------------------------------------------------ Timing and tasks

    /// <summary>
    /// LC-HW-006. Data that arrives every RPI read by a periodic task that runs more often than
    /// that: several scans in a row see the same value, so a timer, a counter or an edge built on
    /// it is slower than it looks. Rockwell's rule of thumb is an RPI of half the task period or
    /// less; this only reports the case that is plainly wrong, an RPI longer than the period.
    /// Inputs reached through an alias tag count as a use of the module.
    /// </summary>
    private static void RpiSlowerThanTask(PlcProject project, CrossReference xref, List<Finding> findings)
    {
        Dictionary<string, TaskInfo> taskOf = TaskByProgram(project);
        if (!taskOf.Values.Any(IsPeriodic))
        {
            return;
        }

        Dictionary<string, ModuleInfo> byName = project.Modules
            .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // Every name logic uses that carries a module's data: the module tags themselves, keyed by
        // their prefix, and the alias tags that point at them.
        var usesByPrefix = new Dictionary<string, List<TagUse>>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, List<TagUse> uses) in xref.Uses)
        {
            if (TagReference.ModuleOf(name) is { } prefix)
            {
                AddAll(usesByPrefix, prefix, uses.Where(u => u.Instruction != "Alias"));
            }
        }

        foreach (TagInfo alias in project.AllTags.Where(t => t.Kind == TagKind.Alias && t.AliasFor is not null))
        {
            string? target = TagReference.BaseNames(alias.AliasFor).FirstOrDefault();
            if (target is not null && TagReference.ModuleOf(target) is { } prefix)
            {
                AddAll(usesByPrefix, prefix, xref.UsesOf(alias));
            }
        }

        foreach (ModuleInfo module in project.Modules.Where(m => !m.IsLocal && !m.Inhibited && m.Connections.Count > 0))
        {
            if (module.Connections.Min(c => c.RpiMs) is not { } rpi
                || !usesByPrefix.TryGetValue(ModuleTags.PrefixOf(module, byName), out List<TagUse>? uses))
            {
                continue;
            }

            ReportStale(module.Name, $"{module.Name} updates every {Ms(rpi)} ms", rpi, uses, taskOf, findings);
        }

        foreach (TagInfo tag in project.Tags.Where(t => t.Kind == TagKind.Consumed && t.Consume?.RpiMs is not null))
        {
            ReportStale(tag.Name, $"Consumed tag {tag.Name} arrives every {Ms(tag.Consume!.RpiMs!.Value)} ms",
                tag.Consume.RpiMs.Value, xref.UsesOf(tag), taskOf, findings);
        }
    }

    private static void ReportStale(
        string subject, string what, double rpi, IEnumerable<TagUse> uses, Dictionary<string, TaskInfo> taskOf, List<Finding> findings)
    {
        foreach (IGrouping<TaskInfo, TagUse> group in uses
            .Where(u => u.Access == TagAccess.Read)
            .Select(u => (Use: u, Task: TaskOfRoutine(u.Routine, taskOf)))
            .Where(x => x.Task is not null && IsPeriodic(x.Task) && x.Task.RateMs < rpi)
            .GroupBy(x => x.Task!, x => x.Use))
        {
            TaskInfo task = group.Key;
            findings.Add(new Finding(
                FindingSeverity.Warning, "LC-HW-006", "Hardware", subject,
                $"{what}, but {task.Name} reads it every {Ms(task.RateMs!.Value)} ms, so consecutive scans see the same value. "
                + "Set the RPI to half the task period or less, or move the logic to a slower task.",
                string.Join(", ", group.Take(5).Select(u => $"{u.Routine} {u.LocationText}")),
                group.Select(FindingSite.Of).ToList()));
        }
    }

    /// <summary>
    /// LC-LOG-010. A controller tag written by logic in two tasks. The higher-priority task can
    /// interrupt the other between two of its rungs, so the lower one can act on a value that
    /// changed under it mid-scan - the classic intermittent fault. Program tags cannot be written
    /// from another program, so only controller scope is checked.
    /// </summary>
    private static void WrittenFromTwoTasks(PlcProject project, CrossReference xref, List<Finding> findings)
    {
        Dictionary<string, TaskInfo> taskOf = TaskByProgram(project);
        if (taskOf.Values.Distinct().Count() < 2)
        {
            return;
        }

        foreach (TagInfo tag in project.Tags)
        {
            List<(TagUse Use, TaskInfo Task)> writes = xref.UsesOf(tag)
                .Where(u => u.Access == TagAccess.Write)
                .Select(u => (Use: u, Task: TaskOfRoutine(u.Routine, taskOf)))
                .Where(x => x.Task is not null)
                .Select(x => (x.Use, x.Task!))
                .ToList();

            List<string> tasks = writes.Select(w => w.Task.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (tasks.Count < 2)
            {
                continue;
            }

            findings.Add(new Finding(
                FindingSeverity.Warning, "LC-LOG-010", "Logic", tag.Name,
                $"{tag.Name} is written from {tasks.Count} tasks ({string.Join(", ", tasks)}). The higher-priority task can "
                + "interrupt the other mid-scan, so logic in the lower one may act on a value that changed under it. "
                + "Write it from one task, or buffer it.",
                string.Join(", ", writes.Take(6).Select(w => $"{w.Use.Routine} {w.Use.LocationText}")),
                writes.Select(w => FindingSite.Of(w.Use)).ToList()));
        }
    }

    // ------------------------------------------------------------------ Status and results

    /// <summary>
    /// LC-COM-003. A GSV that reads a module's status into a tag nothing reads. Somebody meant to
    /// watch the connection; the logic that would notice it dropping was never written.
    /// </summary>
    private static void StatusReadNotUsed(CrossReference xref, List<Finding> findings)
    {
        foreach (InstructionSite site in xref.Instructions.Where(s =>
            s.Instruction.Mnemonic.Equals("GSV", StringComparison.OrdinalIgnoreCase)
            && s.Instruction.Operands.Count >= 4
            && s.Instruction.Operands[0].Equals("Module", StringComparison.OrdinalIgnoreCase)))
        {
            string destination = site.Instruction.Operands[3];
            string? name = TagReference.BaseNames(destination).FirstOrDefault();
            if (name is null)
            {
                continue;
            }

            bool read = xref.Uses
                .Where(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)
                    || p.Key.EndsWith("." + name, StringComparison.OrdinalIgnoreCase))
                .SelectMany(p => p.Value)
                .Any(u => u.Access == TagAccess.Read);

            if (!read)
            {
                string module = site.Instruction.Operands[1];
                findings.Add(new Finding(
                    FindingSeverity.Warning, "LC-COM-003", "Communications", module,
                    $"{site.Routine.QualifiedName} {site.LocationText} reads {module}'s {site.Instruction.Operands[2]} into {destination}, "
                    + "but no logic looks at it. A dropped connection is still silent.",
                    $"{site.Routine.QualifiedName} {site.LocationText}",
                    [FindingSite.Of(site)]));
            }
        }
    }

    /// <summary>
    /// LC-MSG-003. A message that is fired but whose .DN and .ER bits nothing reads. It may never
    /// complete, or fail every time, and the logic carries on with the last data it got.
    /// </summary>
    private static void MessagesWithoutResultHandling(PlcProject project, CrossReference xref, List<Finding> findings)
    {
        foreach (TagInfo tag in project.AllTags.Where(t => t.IsMessage))
        {
            List<InstructionSite> fired = xref.Instructions
                .Where(s => s.Instruction.Mnemonic.Equals("MSG", StringComparison.OrdinalIgnoreCase)
                    && s.Instruction.Operands.Count > 0
                    && string.Equals(TagReference.BaseNames(s.Instruction.Operands[0]).FirstOrDefault(), tag.Name, StringComparison.OrdinalIgnoreCase)
                    && (tag.Scope is null || string.Equals(s.Routine.Owner, tag.Scope, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (fired.Count == 0)
            {
                continue; // LC-MSG-001 already says so
            }

            bool handled = xref.UsesOf(tag).Any(u => u.Access == TagAccess.Read && ReadsResultBit(u.Operand));
            if (!handled)
            {
                findings.Add(new Finding(
                    FindingSeverity.Warning, "LC-MSG-003", "Messages", tag.QualifiedName,
                    $"{tag.QualifiedName} is fired but nothing reads its .DN or .ER bit. If the message fails or never "
                    + "completes, nothing notices, and logic carries on with the last data it received.",
                    string.Join(", ", fired.Select(s => $"{s.Routine.QualifiedName} {s.LocationText}")),
                    fired.Select(FindingSite.Of).ToList()));
            }
        }
    }

    private static bool ReadsResultBit(string operand)
    {
        string o = operand.Replace(" ", string.Empty, StringComparison.Ordinal);
        return o.Contains(".DN", StringComparison.OrdinalIgnoreCase)
            || o.Contains(".ER", StringComparison.OrdinalIgnoreCase)
            || o.Contains(".ERR", StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ Definitions

    /// <summary>
    /// LC-LOG-009. Data types and Add-On Instructions nothing uses: no tag, member, parameter or
    /// local tag of the type, and no call of the AOI. One finding for all of them, like unused tags.
    /// </summary>
    private static void UnusedDefinitions(PlcProject project, CrossReference xref, List<Finding> findings)
    {
        var usedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? type in project.AllTags.Select(t => t.DataType)
            .Concat(project.DataTypes.SelectMany(d => d.Members).Select(m => m.DataType))
            .Concat(project.AddOnInstructions.SelectMany(a => a.Parameters.Concat(a.LocalTags)).Select(t => t.DataType)))
        {
            if (type is not null)
            {
                usedTypes.Add(type);
            }
        }

        var called = new HashSet<string>(xref.Instructions.Select(s => s.Instruction.Mnemonic), StringComparer.OrdinalIgnoreCase);

        List<string> unused = project.DataTypes
            .Where(d => !usedTypes.Contains(d.Name))
            .Select(d => $"data type {d.Name}")
            .Concat(project.AddOnInstructions
                .Where(a => !called.Contains(a.Name) && !usedTypes.Contains(a.Name))
                .Select(a => $"Add-On {a.Name}"))
            .ToList();

        if (unused.Count > 0)
        {
            findings.Add(new Finding(
                FindingSeverity.Info, "LC-LOG-009", "Logic", $"{unused.Count} unused definitions",
                $"Nothing in this project uses these, so they can probably be deleted: {string.Join(", ", unused)}."));
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Program name to the task that schedules it.</summary>
    internal static Dictionary<string, TaskInfo> TaskByProgram(PlcProject project)
    {
        var map = new Dictionary<string, TaskInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (TaskInfo task in project.Tasks)
        {
            foreach (string program in task.ScheduledPrograms)
            {
                map.TryAdd(program, task);
            }
        }

        return map;
    }

    /// <summary>The task a "Program/Routine" runs in, or null for an AOI's logic or an unscheduled program.</summary>
    private static TaskInfo? TaskOfRoutine(string qualifiedRoutine, Dictionary<string, TaskInfo> taskOf)
    {
        int slash = qualifiedRoutine.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 && taskOf.TryGetValue(qualifiedRoutine[..slash], out TaskInfo? task) ? task : null;
    }

    private static bool IsPeriodic(TaskInfo task) =>
        string.Equals(task.Type, "PERIODIC", StringComparison.OrdinalIgnoreCase) && task.RateMs is > 0;

    private static void AddAll(Dictionary<string, List<TagUse>> map, string key, IEnumerable<TagUse> uses)
    {
        if (!map.TryGetValue(key, out List<TagUse>? list))
        {
            list = [];
            map[key] = list;
        }

        list.AddRange(uses);
    }

    private static string Ms(double ms) => ms.ToString("0.###", CultureInfo.InvariantCulture);
}
