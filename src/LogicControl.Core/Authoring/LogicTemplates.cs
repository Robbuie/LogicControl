using System.Globalization;

namespace LogicControl.Core.Authoring;

/// <summary>
/// Generators for the logic every line has a dozen of: a motor starter, a two-position valve, a
/// latched alarm, an analog input with limits, a heartbeat between controllers.
///
/// <para>Each one makes a UDT once, a tag of it per instance, and a routine with the same rungs for
/// every instance - commented, in the house shape - or, for the motor, an Add-On Instruction and one
/// call per instance instead. The result is an ordinary <see cref="DevelopmentSet"/>: it is checked,
/// edited and exported like anything written by hand.</para>
///
/// <para>The rungs are deliberately plain ladder that any controls engineer would recognise, with no
/// plant-specific I/O: mapping a starter's auxiliary contact to a real input is a rung the engineer
/// adds, because which input it is lives in the drawings, not in this tool.</para>
/// </summary>
public static class LogicTemplates
{
    public static IReadOnlyList<LogicTemplate> All { get; } =
    [
        new("motor", "Motor starter",
            "Start/stop seal-in with fail-to-start timer, overload and fault latch with reset.",
            "Motor", [new("FailMs", "Fail-to-start time (ms)", "5000")], Motor),
        new("motor-aoi", "Motor starter (Add-On)",
            "The same motor logic as an Add-On Instruction, with one call per motor.",
            "Motor_Ctl", [new("FailMs", "Fail-to-start time (ms)", "5000")], MotorAoi),
        new("valve", "Two-position valve",
            "Open command, open/closed limit switches, travel-time fault and reset.",
            "Valve", [new("TravelMs", "Travel time allowed (ms)", "10000")], Valve),
        new("alarm", "Latched alarm",
            "On-delay, latch, acknowledge and reset, with an unacknowledged bit for the HMI.",
            "Alarm", [new("DelayMs", "On-delay (ms)", "2000")], Alarm),
        new("analog", "Analog input",
            "SCP scaling from raw to engineering units with high and low limits.",
            "AnalogIn", [], Analog),
        new("heartbeat", "Heartbeat",
            "A counter this controller sends and a watchdog on the one it receives.",
            "Heartbeat", [new("PulseMs", "Send period (ms)", "1000"), new("TimeoutMs", "Receive timeout (ms)", "3000")], Heartbeat),
    ];

    public static LogicTemplate? Find(string key) =>
        All.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ motor

    private static DevelopmentSet Motor(TemplateRequest r)
    {
        string ms = r.Value("FailMs", "5000");
        var udt = new UdtDraft
        {
            Name = r.TypeName,
            Description = "Motor starter - commands, feedback and status",
            Members =
            [
                new("Cmd_Start", "BOOL", "Start command (momentary)"),
                new("Cmd_Stop", "BOOL", "Stop command (momentary, true to stop)"),
                new("Run_FB", "BOOL", "Running feedback - starter auxiliary contact"),
                new("Overload", "BOOL", "Overload tripped"),
                new("Reset", "BOOL", "Fault reset"),
                new("Run", "BOOL", "Run output to the starter"),
                new("Fault", "BOOL", "Latched fault"),
                new("FailToStart", "TIMER", "Run commanded without feedback"),
            ],
        };

        return Build(r, udt, m =>
        [
            new($"[XIC({m}.Cmd_Start),XIC({m}.Run)]XIO({m}.Cmd_Stop)XIO({m}.Fault)OTE({m}.Run);", $"{m} - run: start/stop seal-in, dropped by stop or fault"),
            new($"XIC({m}.Run)XIO({m}.Run_FB)TON({m}.FailToStart,{ms},0);", $"{m} - fail to start: run commanded, no feedback for {ms} ms"),
            new($"[XIC({m}.FailToStart.DN),XIC({m}.Overload)]OTL({m}.Fault);", $"{m} - latch the fault"),
            new($"XIC({m}.Reset)XIO({m}.Overload)OTU({m}.Fault);", $"{m} - reset, once the overload has cleared"),
        ]);
    }

    private static DevelopmentSet MotorAoi(TemplateRequest r)
    {
        string ms = r.Value("FailMs", "5000");
        var aoi = new AoiDraft
        {
            Name = r.TypeName,
            Description = "Motor starter: seal-in, fail to start, overload, fault latch and reset",
            Parameters =
            [
                new("Cmd_Start", "BOOL", "Input", true, "Start command (momentary)"),
                new("Cmd_Stop", "BOOL", "Input", true, "Stop command (momentary, true to stop)"),
                new("Run_FB", "BOOL", "Input", true, "Running feedback"),
                new("Overload", "BOOL", "Input", true, "Overload tripped"),
                new("Reset", "BOOL", "Input", false, "Fault reset"),
                new("Run", "BOOL", "Output", false, "Run output to the starter"),
                new("Fault", "BOOL", "Output", false, "Latched fault"),
                new("FailTime", "DINT", "Input", false, "Fail-to-start time (ms)"),
            ],
            LocalTags = [new("FailToStart", "TIMER", "Run commanded without feedback")],
            Logic =
            [
                new("GRT(FailTime,0)MOV(FailTime,FailToStart.PRE);", "Fail-to-start preset from the parameter, when one is set"),
                new("[XIC(Cmd_Start),XIC(Run)]XIO(Cmd_Stop)XIO(Fault)OTE(Run);", "Start/stop seal-in, dropped by stop or fault"),
                new($"XIC(Run)XIO(Run_FB)TON(FailToStart,{ms},0);", "Fail to start"),
                new("[XIC(FailToStart.DN),XIC(Overload)]OTL(Fault);", "Latch the fault"),
                new("XIC(Reset)XIO(Overload)OTU(Fault);", "Reset, once the overload has cleared"),
            ],
        };

        var set = new DevelopmentSet { AddOnInstructions = [aoi] };
        AddProgram(set, r);
        var routine = new RoutineDraft { Name = r.RoutineName, Program = r.Program, Description = $"{aoi.Name} calls - generated by LogicControl" };

        foreach (string m in r.Instances)
        {
            set.Tags.Add(new TagDraft(m, aoi.Name, $"{m} motor", r.TagProgram));
            set.Tags.Add(new TagDraft($"{m}_Start", "BOOL", $"{m} start command", r.TagProgram));
            set.Tags.Add(new TagDraft($"{m}_Stop", "BOOL", $"{m} stop command", r.TagProgram));
            set.Tags.Add(new TagDraft($"{m}_RunFB", "BOOL", $"{m} running feedback", r.TagProgram));
            set.Tags.Add(new TagDraft($"{m}_OL", "BOOL", $"{m} overload", r.TagProgram));
            routine.Rungs.Add(new($"{aoi.Name}({m},{m}_Start,{m}_Stop,{m}_RunFB,{m}_OL);", $"{m} - motor"));
        }

        set.Routines.Add(routine);
        return set;
    }

    // ------------------------------------------------------------------ valve

    private static DevelopmentSet Valve(TemplateRequest r)
    {
        string ms = r.Value("TravelMs", "10000");
        var udt = new UdtDraft
        {
            Name = r.TypeName,
            Description = "Two-position valve with limit switches",
            Members =
            [
                new("Cmd_Open", "BOOL", "Open command (maintained)"),
                new("ZSO", "BOOL", "Open limit switch"),
                new("ZSC", "BOOL", "Closed limit switch"),
                new("Reset", "BOOL", "Fault reset"),
                new("Out_Open", "BOOL", "Solenoid output"),
                new("Opened", "BOOL", "Confirmed open"),
                new("Closed", "BOOL", "Confirmed closed"),
                new("Fault", "BOOL", "Latched travel fault"),
                new("Travel", "TIMER", "Travel time"),
            ],
        };

        return Build(r, udt, v =>
        [
            new($"XIC({v}.Cmd_Open)XIO({v}.Fault)OTE({v}.Out_Open);", $"{v} - solenoid follows the open command unless faulted"),
            new($"XIC({v}.ZSO)XIO({v}.ZSC)OTE({v}.Opened);", $"{v} - confirmed open"),
            new($"XIC({v}.ZSC)XIO({v}.ZSO)OTE({v}.Closed);", $"{v} - confirmed closed"),
            new($"[XIC({v}.Out_Open)XIO({v}.Opened),XIO({v}.Out_Open)XIO({v}.Closed)]TON({v}.Travel,{ms},0);", $"{v} - travelling: commanded position not confirmed"),
            new($"XIC({v}.Travel.DN)OTL({v}.Fault);", $"{v} - travel fault after {ms} ms"),
            new($"XIC({v}.Reset)OTU({v}.Fault);", $"{v} - reset"),
        ]);
    }

    // ------------------------------------------------------------------ alarm

    private static DevelopmentSet Alarm(TemplateRequest r)
    {
        string ms = r.Value("DelayMs", "2000");
        var udt = new UdtDraft
        {
            Name = r.TypeName,
            Description = "Latched alarm with acknowledge",
            Members =
            [
                new("Input", "BOOL", "Alarm condition"),
                new("Ack", "BOOL", "Acknowledge (HMI)"),
                new("Reset", "BOOL", "Reset (HMI)"),
                new("Active", "BOOL", "Condition present for longer than the delay"),
                new("Latched", "BOOL", "Has been active since the last reset"),
                new("Unacked", "BOOL", "Active and not acknowledged"),
                new("OneShot", "BOOL", "Storage bit for the rising edge"),
                new("Delay", "TIMER", "On-delay"),
            ],
        };

        return Build(r, udt, a =>
        [
            new($"XIC({a}.Input)TON({a}.Delay,{ms},0);", $"{a} - on-delay"),
            new($"XIC({a}.Delay.DN)OTE({a}.Active);", $"{a} - active"),
            new($"XIC({a}.Active)ONS({a}.OneShot)[OTL({a}.Latched),OTL({a}.Unacked)];", $"{a} - latch and require an acknowledge on each new occurrence"),
            new($"XIC({a}.Ack)OTU({a}.Unacked);", $"{a} - acknowledge"),
            new($"XIC({a}.Reset)XIO({a}.Active)XIO({a}.Unacked)OTU({a}.Latched);", $"{a} - reset once cleared and acknowledged"),
        ]);
    }

    // ------------------------------------------------------------------ analog

    private static DevelopmentSet Analog(TemplateRequest r)
    {
        var udt = new UdtDraft
        {
            Name = r.TypeName,
            Description = "Analog input scaled to engineering units",
            Members =
            [
                new("Raw", "REAL", "Raw counts from the input card"),
                new("RawMin", "REAL", "Raw at the bottom of the range"),
                new("RawMax", "REAL", "Raw at the top of the range"),
                new("EuMin", "REAL", "Engineering units at RawMin"),
                new("EuMax", "REAL", "Engineering units at RawMax"),
                new("Value", "REAL", "Scaled value"),
                new("HiLimit", "REAL", "High limit"),
                new("LoLimit", "REAL", "Low limit"),
                new("Hi", "BOOL", "Above the high limit"),
                new("Lo", "BOOL", "Below the low limit"),
            ],
        };

        return Build(r, udt, a =>
        [
            new($"NEQ({a}.RawMax,{a}.RawMin)SCP({a}.Raw,{a}.RawMin,{a}.RawMax,{a}.EuMin,{a}.EuMax,{a}.Value);",
                $"{a} - scale. Set RawMin/RawMax and EuMin/EuMax on the tag; the NEQ keeps an unconfigured input from dividing by zero"),
            new($"GRT({a}.Value,{a}.HiLimit)OTE({a}.Hi);", $"{a} - high"),
            new($"LES({a}.Value,{a}.LoLimit)OTE({a}.Lo);", $"{a} - low"),
        ]);
    }

    // ------------------------------------------------------------------ heartbeat

    private static DevelopmentSet Heartbeat(TemplateRequest r)
    {
        string pulse = r.Value("PulseMs", "1000");
        string timeout = r.Value("TimeoutMs", "3000");
        var udt = new UdtDraft
        {
            Name = r.TypeName,
            Description = "Heartbeat between controllers",
            Members =
            [
                new("Out", "DINT", "Counter this controller sends - map to a produced tag or MSG"),
                new("In", "DINT", "Counter received from the partner"),
                new("Last", "DINT", "Last value received"),
                new("Healthy", "BOOL", "Partner counter changed within the timeout"),
                new("Pulse", "TIMER", "Send period"),
                new("Watchdog", "TIMER", "Receive timeout"),
            ],
        };

        return Build(r, udt, h =>
        [
            new($"XIO({h}.Pulse.DN)TON({h}.Pulse,{pulse},0);", $"{h} - send period"),
            new($"XIC({h}.Pulse.DN)ADD({h}.Out,1,{h}.Out);", $"{h} - count up; Logix wraps a DINT, so no reset is needed"),
            new($"NEQ({h}.In,{h}.Last)[MOV({h}.In,{h}.Last),RES({h}.Watchdog)];", $"{h} - partner counter moved"),
            new($"XIO({h}.Watchdog.DN)TON({h}.Watchdog,{timeout},0);", $"{h} - receive watchdog"),
            new($"XIO({h}.Watchdog.DN)OTE({h}.Healthy);", $"{h} - healthy while the partner keeps counting"),
        ]);
    }

    // ------------------------------------------------------------------ shared

    private static DevelopmentSet Build(TemplateRequest r, UdtDraft udt, Func<string, RungDraft[]> rungsFor)
    {
        var set = new DevelopmentSet { DataTypes = [udt] };
        AddProgram(set, r);

        var routine = new RoutineDraft
        {
            Name = r.RoutineName,
            Program = r.Program,
            Description = $"{udt.Name} logic for {string.Join(", ", r.Instances)} - generated by LogicControl",
        };

        foreach (string instance in r.Instances)
        {
            set.Tags.Add(new TagDraft(instance, udt.Name, $"{instance} {udt.Name.ToLowerInvariant()}", r.TagProgram));
            routine.Rungs.AddRange(rungsFor(instance));
        }

        set.Routines.Add(routine);
        return set;
    }

    private static void AddProgram(DevelopmentSet set, TemplateRequest r)
    {
        if (r.CreateProgram)
        {
            set.Programs.Add(new ProgramDraft { Name = r.Program, MainRoutineName = r.RoutineName, Task = r.Task });
        }
    }
}

/// <summary>A template: what it makes and the settings it asks for beyond names.</summary>
public sealed class LogicTemplate(
    string key,
    string title,
    string summary,
    string defaultTypeName,
    IReadOnlyList<TemplateSetting> settings,
    Func<TemplateRequest, DevelopmentSet> generate)
{
    public string Key { get; } = key;

    public string Title { get; } = title;

    public string Summary { get; } = summary;

    /// <summary>The UDT or AOI name it creates unless told otherwise.</summary>
    public string DefaultTypeName { get; } = defaultTypeName;

    public IReadOnlyList<TemplateSetting> Settings { get; } = settings;

    public DevelopmentSet Generate(TemplateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Instances.Count == 0)
        {
            throw new ArgumentException("Name at least one instance.", nameof(request));
        }

        return generate(request);
    }

    public override string ToString() => Title;
}

/// <summary>A number or name a template asks for, with its default.</summary>
public sealed record TemplateSetting(string Key, string Label, string Default);

/// <summary>What to generate: the instances, where they go, and the template's own settings.</summary>
public sealed class TemplateRequest
{
    /// <summary>Instance names - M101, M102... - one tag and one block of rungs each.</summary>
    public List<string> Instances { get; set; } = [];

    public string TypeName { get; set; } = string.Empty;

    public string Program { get; set; } = "MainProgram";

    public string RoutineName { get; set; } = "Generated";

    /// <summary>Tags in the program (true) or at controller scope (false, the default - HMIs read them there).</summary>
    public bool ProgramScopeTags { get; set; }

    /// <summary>Also create the program, for a project that does not have it yet.</summary>
    public bool CreateProgram { get; set; }

    /// <summary>When creating the program: the task it is scheduled in.</summary>
    public string? Task { get; set; }

    public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? TagProgram => ProgramScopeTags ? Program : null;

    public string Value(string key, string fallback) =>
        Values.TryGetValue(key, out string? v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : fallback;

    /// <summary>
    /// Splits "M101, M102 M103" or "M101-M105" into names. A range keeps the prefix and the number
    /// of digits: P07-P10 gives P07, P08, P09, P10.
    /// </summary>
    public static List<string> ParseInstances(string? text)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return names;
        }

        foreach (string token in text.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            int dash = token.IndexOf('-', StringComparison.Ordinal);
            if (dash > 0 && TrySplitNumber(token[..dash], out string prefix, out string from)
                && TrySplitNumber(token[(dash + 1)..], out string prefix2, out string to)
                && (prefix2.Length == 0 || string.Equals(prefix, prefix2, StringComparison.OrdinalIgnoreCase))
                && int.TryParse(from, NumberStyles.None, CultureInfo.InvariantCulture, out int a)
                && int.TryParse(to, NumberStyles.None, CultureInfo.InvariantCulture, out int b)
                && b >= a && b - a < 500)
            {
                for (int i = a; i <= b; i++)
                {
                    names.Add(prefix + i.ToString(CultureInfo.InvariantCulture).PadLeft(from.Length, '0'));
                }

                continue;
            }

            names.Add(token);
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool TrySplitNumber(string s, out string prefix, out string number)
    {
        int i = s.Length;
        while (i > 0 && char.IsAsciiDigit(s[i - 1]))
        {
            i--;
        }

        prefix = s[..i];
        number = s[i..];
        return number.Length > 0;
    }
}
