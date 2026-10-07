using System.Text.Json;
using System.Text.Json.Serialization;

namespace LogicControl.Core.Authoring;

/// <summary>
/// Everything being written in one sitting: data types, AOIs, tags, programs and routines, and
/// the controller they are meant for.
///
/// <para>Saved as a small JSON file (<c>.lcdev</c>) so work survives closing the app, and turned
/// into L5X by <see cref="L5xWriter"/> - either as import files Studio 5000 brings into an open
/// project, or merged into a whole-project L5X by <see cref="ProjectMerger"/>.</para>
/// </summary>
public sealed class DevelopmentSet
{
    public const string FileExtension = ".lcdev";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Written into every export's Controller element. Studio 5000 ignores it on import.</summary>
    public string ControllerName { get; set; } = "LogicControl";

    /// <summary>
    /// The Studio 5000 version stamped on exports, e.g. "32.00". An import file stamped newer than
    /// the Studio 5000 opening it is refused, so this defaults low and follows the open project.
    /// </summary>
    public string SoftwareRevision { get; set; } = "32.00";

    public List<UdtDraft> DataTypes { get; set; } = [];

    public List<AoiDraft> AddOnInstructions { get; set; } = [];

    public List<TagDraft> Tags { get; set; } = [];

    public List<ProgramDraft> Programs { get; set; } = [];

    public List<RoutineDraft> Routines { get; set; } = [];

    /// <summary>Generic Ethernet modules to add to the I/O tree. Written only into a project copy.</summary>
    public List<ModuleDraft> Modules { get; set; } = [];

    /// <summary>
    /// Every saved step of the work, oldest first - see <see cref="History.RevisionHistory"/>. Lives
    /// in the .lcdev file so the history survives closing the app; never part of a snapshot.
    /// </summary>
    public List<History.RevisionRecord>? History { get; set; }

    [JsonIgnore]
    public bool IsEmpty =>
        DataTypes.Count == 0 && AddOnInstructions.Count == 0 && Tags.Count == 0 && Programs.Count == 0 && Routines.Count == 0
        && Modules.Count == 0;

    /// <summary>Adds everything in <paramref name="other"/>, replacing drafts of the same name.</summary>
    public void Merge(DevelopmentSet other)
    {
        ArgumentNullException.ThrowIfNull(other);

        Replace(DataTypes, other.DataTypes, d => d.Name);
        Replace(AddOnInstructions, other.AddOnInstructions, a => a.Name);
        Replace(Tags, other.Tags, t => t.QualifiedName);
        Replace(Programs, other.Programs, p => p.Name);
        Replace(Routines, other.Routines, r => r.QualifiedName);
        Replace(Modules, other.Modules, m => m.Name);
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>
    /// The drafts alone, compact, without the history - what one revision stores. Two sets with
    /// the same drafts give the same text, which is how "nothing changed" is told.
    /// </summary>
    public string ToSnapshot() => JsonSerializer.Serialize(WithoutHistory(), Snapshot);

    /// <summary>A set from a snapshot. The history is not part of it; the caller keeps its own.</summary>
    public static DevelopmentSet FromSnapshot(string snapshot)
    {
        DevelopmentSet set = FromJson(snapshot);
        set.History = null;
        return set;
    }

    /// <summary>A deep copy of the drafts, with no history.</summary>
    public DevelopmentSet Clone() => FromSnapshot(ToSnapshot());

    private DevelopmentSet WithoutHistory() => new()
    {
        ControllerName = ControllerName,
        SoftwareRevision = SoftwareRevision,
        DataTypes = DataTypes,
        AddOnInstructions = AddOnInstructions,
        Tags = Tags,
        Programs = Programs,
        Routines = Routines,
        Modules = Modules,
    };

    private static readonly JsonSerializerOptions Snapshot = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static DevelopmentSet FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<DevelopmentSet>(json, Json) ?? new DevelopmentSet();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Not a LogicControl development file: {ex.Message}", ex);
        }
    }

    public void Save(string path) => File.WriteAllText(path, ToJson());

    public static DevelopmentSet Load(string path) => FromJson(File.ReadAllText(path));

    private static void Replace<T>(List<T> into, IEnumerable<T> from, Func<T, string> key)
    {
        foreach (T item in from)
        {
            int at = into.FindIndex(x => string.Equals(key(x), key(item), StringComparison.OrdinalIgnoreCase));
            if (at >= 0)
            {
                into[at] = item;
            }
            else
            {
                into.Add(item);
            }
        }
    }
}
