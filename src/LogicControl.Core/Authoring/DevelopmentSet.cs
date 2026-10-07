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

    [JsonIgnore]
    public bool IsEmpty =>
        DataTypes.Count == 0 && AddOnInstructions.Count == 0 && Tags.Count == 0 && Programs.Count == 0 && Routines.Count == 0;

    /// <summary>Adds everything in <paramref name="other"/>, replacing drafts of the same name.</summary>
    public void Merge(DevelopmentSet other)
    {
        ArgumentNullException.ThrowIfNull(other);

        Replace(DataTypes, other.DataTypes, d => d.Name);
        Replace(AddOnInstructions, other.AddOnInstructions, a => a.Name);
        Replace(Tags, other.Tags, t => t.QualifiedName);
        Replace(Programs, other.Programs, p => p.Name);
        Replace(Routines, other.Routines, r => r.QualifiedName);
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

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
