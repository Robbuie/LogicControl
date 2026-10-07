using System.Globalization;

namespace LogicControl.Core.Authoring;

/// <summary>
/// The rules Logix applies to names, and the built-in data types with the members a rung can
/// address on them.
///
/// <para>Only what writing needs: enough to say "Motor__Run is not a legal name" or "TIMER has no
/// member DONE" before Studio 5000 says it on import. The structure list is the common set -
/// TIMER, COUNTER, CONTROL, MESSAGE, STRING and friends - and a type missing from it is treated as
/// unknown rather than wrong.</para>
/// </summary>
public static class LogixTypes
{
    /// <summary>Logix's limit on a tag, type, routine or program name.</summary>
    public const int MaxNameLength = 40;

    private static readonly Dictionary<string, AtomicType> Atomic = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BOOL"] = new("BOOL", 1, "Decimal"),
        ["SINT"] = new("SINT", 8, "Decimal"),
        ["INT"] = new("INT", 16, "Decimal"),
        ["DINT"] = new("DINT", 32, "Decimal"),
        ["LINT"] = new("LINT", 64, "Decimal"),
        ["USINT"] = new("USINT", 8, "Decimal"),
        ["UINT"] = new("UINT", 16, "Decimal"),
        ["UDINT"] = new("UDINT", 32, "Decimal"),
        ["ULINT"] = new("ULINT", 64, "Decimal"),
        ["REAL"] = new("REAL", 32, "Float"),
        ["LREAL"] = new("LREAL", 64, "Float"),
    };

    /// <summary>Built-in structures and their members, for member checks and the type pickers.</summary>
    private static readonly Dictionary<string, string[]> Structures = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TIMER"] = ["PRE", "ACC", "EN", "TT", "DN"],
        ["COUNTER"] = ["PRE", "ACC", "CU", "CD", "DN", "OV", "UN"],
        ["CONTROL"] = ["LEN", "POS", "EN", "EU", "DN", "EM", "ER", "UL", "IN", "FD"],
        ["STRING"] = ["LEN", "DATA"],
        ["MESSAGE"] = ["Flags", "EW", "ER", "DN", "ST", "EN", "TO", "EN_CC", "ERR", "EXERR", "ERR_SRC", "DN_LEN", "REQ_LEN", "DestinationLink", "DestinationNode", "SourceLink", "Class", "Attribute", "Instance", "LocalIndex", "Channel", "Rack", "Group", "Slot", "Path", "RemoteIndex", "RemoteElement", "UnconnectedTimeout", "ConnectionRate", "TimeoutMultiplier"],
        ["PID"] = ["CTL", "SP", "KP", "KI", "KD", "BIAS", "MAXS", "MINS", "DB", "SO", "MAXO", "MINO", "UPD", "PV", "ERR", "OUT", "PVH", "PVL", "DVP", "DVN", "PVDB", "DVDB", "MAXI", "MINI", "TIE", "MAXCV", "MINCV", "MINTIE", "MAXTIE", "DATA", "EN", "CT", "CL", "PVT", "DOE", "SWM", "CA", "MO", "PE", "NDF", "NOBC", "NOZC", "INI", "SPOR", "OLL", "OLH", "EWD", "DVNA", "DVPA", "PVLA", "PVHA"],
        ["ALARM_DIGITAL"] = ["EnableIn", "In", "InFault", "Condition", "AckRequired", "Latched", "ProgAck", "OperAck", "ProgReset", "OperReset", "ProgSuppress", "OperSuppress", "ProgUnsuppress", "OperUnsuppress", "ProgDisable", "OperDisable", "ProgEnable", "OperEnable", "AlarmCountReset", "UseProgTime", "ProgTime", "Severity", "MinDurationPRE", "EnableOut", "InAlarm", "Acked", "InAlarmUnack", "Suppressed", "Disabled", "MinDurationACC", "AlarmCount", "Status", "InstructFault"],
    };

    /// <summary>Every built-in type a draft can pick, atomic first.</summary>
    public static IReadOnlyList<string> BuiltInTypes { get; } =
        [.. Atomic.Keys, .. Structures.Keys.Where(k => !k.Equals("ALARM_DIGITAL", StringComparison.OrdinalIgnoreCase)), "ALARM_DIGITAL"];

    public static bool IsAtomic(string? type) => type is not null && Atomic.ContainsKey(type);

    public static bool IsBool(string? type) => string.Equals(type, "BOOL", StringComparison.OrdinalIgnoreCase);

    public static bool IsBuiltIn(string? type) => type is not null && (Atomic.ContainsKey(type) || Structures.ContainsKey(type));

    /// <summary>The members of a built-in structure, or null for an atomic or unknown type.</summary>
    public static IReadOnlyList<string>? MembersOf(string? type) =>
        type is not null && Structures.TryGetValue(type, out string[]? members) ? members : null;

    /// <summary>The bit width of an integer type, for .0 - .31 style bit addressing. Null for anything else.</summary>
    public static int? BitsOf(string? type) =>
        type is not null && Atomic.TryGetValue(type, out AtomicType? a) && a.Radix == "Decimal" && a.Bits > 1 ? a.Bits : null;

    /// <summary>The radix L5X writes for a member or tag of this type, or null for a structure.</summary>
    public static string? RadixOf(string? type) =>
        type is not null && Atomic.TryGetValue(type, out AtomicType? a) ? a.Radix : null;

    /// <summary>The canonical spelling - "dint" becomes "DINT"; a UDT name is returned as given.</summary>
    public static string Canonical(string type)
    {
        if (Atomic.TryGetValue(type, out AtomicType? a))
        {
            return a.Name;
        }

        return Structures.Keys.FirstOrDefault(k => k.Equals(type, StringComparison.OrdinalIgnoreCase)) ?? type;
    }

    /// <summary>
    /// Why <paramref name="name"/> is not a legal Logix name, or null when it is. Letters, digits
    /// and underscores; starts with a letter or underscore; at most 40 characters; no two
    /// underscores in a row and none at the end.
    /// </summary>
    public static string? NameProblem(string? name, string what = "Name")
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return $"{what} is empty.";
        }

        if (name.Length > MaxNameLength)
        {
            return $"{what} '{name}' is {name.Length} characters; Logix allows {MaxNameLength}.";
        }

        if (!(char.IsAsciiLetter(name[0]) || name[0] == '_'))
        {
            return $"{what} '{name}' must start with a letter or an underscore.";
        }

        foreach (char c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                return $"{what} '{name}' contains '{c}'; only letters, digits and underscores are allowed.";
            }
        }

        if (name.Contains("__", StringComparison.Ordinal))
        {
            return $"{what} '{name}' has two underscores in a row, which Logix does not allow.";
        }

        if (name.EndsWith('_'))
        {
            return $"{what} '{name}' ends with an underscore, which Logix does not allow.";
        }

        return null;
    }

    /// <summary>
    /// Parses "DINT", "DINT[10]", "REAL[4,8]" or "BOOL[32]" into a type and its dimensions as
    /// L5X writes them ("10", "4 8"). Returns false for anything else.
    /// </summary>
    public static bool TryParseTypeSpec(string? spec, out string type, out string? dimensions)
    {
        type = string.Empty;
        dimensions = null;
        if (string.IsNullOrWhiteSpace(spec))
        {
            return false;
        }

        string s = spec.Trim();
        int open = s.IndexOf('[', StringComparison.Ordinal);
        if (open < 0)
        {
            type = s;
            return NameProblem(s) is null;
        }

        if (!s.EndsWith(']'))
        {
            return false;
        }

        type = s[..open].Trim();
        string[] parts = s[(open + 1)..^1].Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length is 0 or > 3 || parts.Any(p => !int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n <= 0))
        {
            return false;
        }

        dimensions = string.Join(' ', parts);
        return NameProblem(type) is null;
    }

    private sealed record AtomicType(string Name, int Bits, string Radix);
}
