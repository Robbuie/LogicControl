using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace LogicControl.Core.Authoring.History;

/// <summary>
/// What the revisions of one development set are made of: every piece of every snapshot, each
/// stored once under a name worked out from its content.
///
/// <para><b>Why pieces.</b> A revision is still a whole snapshot - going back to step 12 is still
/// reading step 12, with nothing replayed - but a snapshot is cut into pieces and a piece another
/// revision already holds is not stored again. Editing one rung of a 2,000-rung routine stores
/// the chunk of about twenty rungs around it and the routine's list of chunks, not the routine.
/// Before this every revision held the whole set as text, and a few hundred edits to a big routine
/// held hundreds of copies of it.</para>
///
/// <para><b>How a snapshot is cut.</b> The snapshot's JSON is walked bottom up. An object whose
/// JSON is longer than <see cref="InlineLimit"/> becomes a piece of its own and is replaced by
/// <c>{"$ref":"name"}</c>. An array longer than <see cref="ChunkMax"/> is cut into chunks and
/// replaced by <c>{"$chunks":["name",...]}</c>; where a chunk ends is decided by the content of
/// the element it ends on (on average every <see cref="ChunkAverage"/> elements), so inserting
/// or deleting a rung changes only the chunk it is in, not every chunk after it. The top of the
/// snapshot is always a piece; its name is the revision's <see cref="RevisionRecord.Root"/>.
/// No draft has a property whose name starts with <c>$</c>, so the two markers cannot be mistaken
/// for drafts.</para>
///
/// <para><b>Names.</b> The first 16 hex digits of the piece's SHA-256. Two different pieces with
/// the same name are told apart when stored (<c>name~1</c>, <c>name~2</c>...), so a clash costs a
/// suffix, never a wrong revision.</para>
///
/// <para>Pieces nothing uses any more - once the oldest revisions are dropped past
/// <see cref="RevisionHistory.Limit"/> - are removed by <see cref="Prune"/>.</para>
/// </summary>
public sealed class RevisionStore
{
    /// <summary>An object whose JSON is longer than this is stored as a piece of its own.</summary>
    internal const int InlineLimit = 256;

    /// <summary>An array with more elements than this is cut into chunks.</summary>
    internal const int ChunkMax = 64;

    /// <summary>The fewest elements a chunk ends on, unless the array ends first.</summary>
    internal const int ChunkMin = 4;

    /// <summary>A chunk ends after an element whose hash is a multiple of this, so chunks average about this many more than <see cref="ChunkMin"/>.</summary>
    internal const int ChunkAverage = 16;

    private const string RefKey = "$ref";
    private const string ChunksKey = "$chunks";

    /// <summary>The store over <paramref name="pieces"/> - the set's <see cref="DevelopmentSet.HistoryStore"/>, shared, not copied.</summary>
    public RevisionStore(Dictionary<string, string> pieces)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        Pieces = pieces;
    }

    /// <summary>Every piece by name: compact JSON.</summary>
    public Dictionary<string, string> Pieces { get; }

    /// <summary>Stores <paramref name="snapshot"/> (<see cref="DevelopmentSet.ToSnapshot"/>) and returns the name of its top piece. The same snapshot always gets the same name.</summary>
    public string Put(string snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (JsonNode.Parse(snapshot) is not JsonObject root)
        {
            throw new InvalidDataException("A snapshot is a JSON object.");
        }

        return Store(EncodeMembers(root).ToJsonString());
    }

    /// <summary>The snapshot whose top piece is <paramref name="root"/>, put back together.</summary>
    public string Get(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        JsonNode expanded = Expand(JsonNode.Parse(Piece(root)))
            ?? throw new InvalidDataException($"The revision history's piece {root} is empty.");
        return expanded.ToJsonString();
    }

    /// <summary>
    /// Removes every piece that none of <paramref name="roots"/> use, and returns how many went.
    /// </summary>
    public int Prune(IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(roots);
        while (pending.TryPop(out string? name))
        {
            if (used.Add(name) && Pieces.TryGetValue(name, out string? piece))
            {
                CollectNames(JsonNode.Parse(piece), pending);
            }
        }

        List<string> unused = Pieces.Keys.Where(k => !used.Contains(k)).ToList();
        foreach (string name in unused)
        {
            Pieces.Remove(name);
        }

        return unused.Count;
    }

    /// <summary>The characters the pieces take up - for tests and diagnostics.</summary>
    public long Size => Pieces.Sum(p => (long)p.Key.Length + p.Value.Length);

    // ------------------------------------------------------------------ cutting up

    private JsonNode? Encode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                JsonObject encoded = EncodeMembers(o);
                string text = encoded.ToJsonString();
                return text.Length > InlineLimit ? new JsonObject { [RefKey] = Store(text) } : encoded;

            case JsonArray a:
                List<JsonNode?> items = a.Select(Encode).ToList();
                return items.Count > ChunkMax ? new JsonObject { [ChunksKey] = Chunk(items) } : new JsonArray([.. items]);

            default:
                return node?.DeepClone();
        }
    }

    private JsonObject EncodeMembers(JsonObject o)
    {
        var encoded = new JsonObject();
        foreach (KeyValuePair<string, JsonNode?> member in o)
        {
            encoded[member.Key] = Encode(member.Value);
        }

        return encoded;
    }

    private JsonArray Chunk(List<JsonNode?> items)
    {
        var names = new JsonArray();
        var chunk = new JsonArray();
        foreach (JsonNode? item in items)
        {
            string text = item?.ToJsonString() ?? "null";
            chunk.Add(item);
            if (chunk.Count == ChunkMax || (chunk.Count >= ChunkMin && Fnv(text) % ChunkAverage == 0))
            {
                names.Add(Store(chunk.ToJsonString()));
                chunk = [];
            }
        }

        if (chunk.Count > 0)
        {
            names.Add(Store(chunk.ToJsonString()));
        }

        return names;
    }

    private string Store(string piece)
    {
        string name = Name(piece);
        for (int clash = 1; ; clash++)
        {
            if (!Pieces.TryGetValue(name, out string? held))
            {
                Pieces[name] = piece;
                return name;
            }

            if (string.Equals(held, piece, StringComparison.Ordinal))
            {
                return name;
            }

            name = $"{Name(piece)}~{clash}";
        }
    }

    private static string Name(string piece) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(piece)), 0, 8);

    /// <summary>FNV-1a: the same in every process, unlike string.GetHashCode, so chunks fall in the same places after reopening.</summary>
    private static uint Fnv(string text)
    {
        uint hash = 2166136261;
        foreach (char c in text)
        {
            hash = (hash ^ c) * 16777619;
        }

        return hash;
    }

    // ------------------------------------------------------------------ putting back together

    private string Piece(string name) =>
        Pieces.TryGetValue(name, out string? piece)
            ? piece
            : throw new InvalidDataException($"The revision history is missing piece {name}.");

    private JsonNode? Expand(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o when Marker(o, RefKey) is JsonValue name:
                return Expand(JsonNode.Parse(Piece(name.GetValue<string>())));

            case JsonObject o when Marker(o, ChunksKey) is JsonArray names:
                var joined = new JsonArray();
                foreach (JsonNode? name in names)
                {
                    if (JsonNode.Parse(Piece(name!.GetValue<string>())) is JsonArray chunk)
                    {
                        foreach (JsonNode? item in chunk)
                        {
                            joined.Add(Expand(item));
                        }
                    }
                }

                return joined;

            case JsonObject o:
                var expanded = new JsonObject();
                foreach (KeyValuePair<string, JsonNode?> member in o)
                {
                    expanded[member.Key] = Expand(member.Value);
                }

                return expanded;

            case JsonArray a:
                return new JsonArray([.. a.Select(Expand)]);

            default:
                return node?.DeepClone();
        }
    }

    private static void CollectNames(JsonNode? node, Stack<string> into)
    {
        switch (node)
        {
            case JsonObject o when Marker(o, RefKey) is JsonValue name:
                into.Push(name.GetValue<string>());
                break;

            case JsonObject o when Marker(o, ChunksKey) is JsonArray names:
                foreach (JsonNode? name in names)
                {
                    into.Push(name!.GetValue<string>());
                }

                break;

            case JsonObject o:
                foreach (KeyValuePair<string, JsonNode?> member in o)
                {
                    CollectNames(member.Value, into);
                }

                break;

            case JsonArray a:
                foreach (JsonNode? item in a)
                {
                    CollectNames(item, into);
                }

                break;
        }
    }

    /// <summary>The value of <paramref name="key"/> when it is <paramref name="o"/>'s only member.</summary>
    private static JsonNode? Marker(JsonObject o, string key) =>
        o.Count == 1 && o.TryGetPropertyValue(key, out JsonNode? value) ? value : null;
}

/// <summary>
/// Writes <see cref="DevelopmentSet.HistoryStore"/> with each piece as the JSON it is, not as an
/// escaped string, so a .lcdev stays readable; reads each one back as its compact text.
/// </summary>
internal sealed class RevisionPiecesConverter : JsonConverter<Dictionary<string, string>>
{
    public override Dictionary<string, string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("The revision store is a JSON object.");
        }

        var pieces = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string name = reader.GetString() ?? throw new JsonException("A revision piece has no name.");
            reader.Read();
            using var piece = JsonDocument.ParseValue(ref reader);
            pieces[name] = piece.RootElement.GetRawText();
        }

        return pieces;
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, string> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (KeyValuePair<string, string> piece in value)
        {
            writer.WritePropertyName(piece.Key);
            writer.WriteRawValue(piece.Value, skipInputValidation: true);
        }

        writer.WriteEndObject();
    }
}
