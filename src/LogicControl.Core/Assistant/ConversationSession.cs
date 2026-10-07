using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LogicControl.Core.Assistant;

/// <summary>
/// One chat with Claude about the open project: the message history, and the loop that lets
/// Claude call LogicControl's tools until it has an answer.
///
/// <para>Each user message starts a turn. A turn is one or more API requests: Claude answers, and
/// if the answer asks for tools, they run here - reads against the project, drafts into the Develop
/// set - and their results go back in the next request. The turn ends when Claude stops asking, or
/// after <see cref="MaxRoundsPerTurn"/> rounds so a confused model cannot loop all afternoon.</para>
///
/// <para>Prompt caching is on: the system prompt, the tool table and the conversation so far are
/// marked so a follow-up question re-reads them from Anthropic's cache at a tenth of the price
/// rather than paying for the whole project context again.</para>
///
/// <para>Tools run on whatever thread the caller awaited from - in the app, the UI thread - because
/// draft tools change the same development set the Develop tab is bound to. They are fast; the
/// slow part is the network, which is awaited.</para>
/// </summary>
public sealed class ConversationSession(ClaudeClient client, LogicTools tools, AssistantOptions options)
{
    public const int MaxRoundsPerTurn = 24;

    private readonly List<JsonObject> _messages = [];

    /// <summary>Whether this turn has shown any text yet - a new text block after it starts a new paragraph.</summary>
    private bool _textInTurn;

    public AssistantOptions Options { get; } = options;

    /// <summary>Tokens used since the chat started.</summary>
    public Usage TotalUsage { get; private set; } = Usage.Zero;

    public int MessageCount => _messages.Count;

    public void Reset()
    {
        _messages.Clear();
        TotalUsage = Usage.Zero;
    }

    /// <summary>
    /// Sends <paramref name="userText"/> - with <paramref name="context"/>, what the user is looking
    /// at, prepended - and runs the turn to the end. Streams text and tool activity to
    /// <paramref name="sink"/>. Throws <see cref="ClaudeApiException"/> for API failures and
    /// <see cref="OperationCanceledException"/> when stopped; either way the history is left valid
    /// for the next message.
    /// </summary>
    public async Task RunTurnAsync(string userText, string? context, IAssistantSink sink, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userText);
        ArgumentNullException.ThrowIfNull(sink);

        string text = string.IsNullOrWhiteSpace(context) ? userText : $"<context>\n{context.Trim()}\n</context>\n\n{userText}";
        _messages.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        });

        int checkpoint = _messages.Count;
        _textInTurn = false;
        try
        {
            for (int round = 0; round < MaxRoundsPerTurn; round++)
            {
                (JsonArray content, string? stop) = await RequestAsync(sink, cancellationToken).ConfigureAwait(true);
                _messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = content });

                List<JsonObject> calls = content.OfType<JsonObject>().Where(b => (string?)b["type"] == "tool_use").ToList();
                if (stop != "tool_use" || calls.Count == 0)
                {
                    if (stop == "max_tokens")
                    {
                        sink.Notice("The answer hit the length limit and was cut off. Ask it to continue.");
                    }

                    return;
                }

                var results = new JsonArray();
                foreach (JsonObject call in calls)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string id = (string)call["id"]!;
                    string name = (string)call["name"]!;
                    JsonElement input = JsonSerializer.SerializeToElement(call["input"] ?? new JsonObject());

                    sink.ToolStarted(id, name, input);
                    ToolResult result = tools.Execute(name, input);
                    sink.ToolFinished(id, name, result);

                    results.Add(new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = id,
                        ["content"] = result.Text,
                        ["is_error"] = result.IsError,
                    });
                }

                _messages.Add(new JsonObject { ["role"] = "user", ["content"] = results });
            }

            sink.Notice($"Stopped after {MaxRoundsPerTurn} rounds of tool calls. Say 'continue' to let it carry on.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or ClaudeApiException)
        {
            Repair(checkpoint);
            throw;
        }
    }

    /// <summary>
    /// After a stop or a failure, leaves the history in a state the API accepts: every tool_use
    /// answered by a tool_result, and the conversation ending on an assistant message or a user
    /// message that is waiting for one - never two user messages in a row.
    /// </summary>
    private void Repair(int checkpoint)
    {
        if (_messages.Count > 0 && (string?)_messages[^1]["role"] == "assistant"
            && _messages[^1]["content"] is JsonArray content
            && content.OfType<JsonObject>().Where(b => (string?)b["type"] == "tool_use").ToList() is { Count: > 0 } pending)
        {
            _messages.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(pending.Select(p => (JsonNode)new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = (string)p["id"]!,
                    ["content"] = "Stopped by the user before this ran.",
                    ["is_error"] = true,
                }).ToArray()),
            });
        }

        if (_messages.Count > 0 && (string?)_messages[^1]["role"] == "user")
        {
            // Close the turn so the next user message does not follow a user message.
            _messages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = _messages.Count == checkpoint ? "(No answer - the request did not complete.)" : "(Stopped.)" }),
            });
        }
    }

    private async Task<(JsonArray Content, string? StopReason)> RequestAsync(IAssistantSink sink, CancellationToken cancellationToken)
    {
        JsonObject body = BuildRequest();
        var blocks = new SortedDictionary<int, BlockBuilder>();
        string? stop = null;
        Usage usage = Usage.Zero;

        await foreach (StreamEvent e in client.StreamAsync(body, cancellationToken).ConfigureAwait(true))
        {
            switch (e.Type)
            {
                case "message_start":
                    JsonNode? u = e.Data["message"]?["usage"];
                    usage = usage.Add(new Usage(Int(u, "input_tokens"), Int(u, "output_tokens"), Int(u, "cache_read_input_tokens"), Int(u, "cache_creation_input_tokens")));
                    break;

                case "content_block_start":
                {
                    int index = Int(e.Data, "index");
                    JsonObject block = e.Data["content_block"] as JsonObject ?? [];
                    var builder = new BlockBuilder((string?)block["type"] ?? "text", (string?)block["id"], (string?)block["name"]);
                    blocks[index] = builder;
                    if (block["text"] is JsonValue t && t.GetValue<string>() is { Length: > 0 } initial)
                    {
                        builder.Text.Append(initial);
                        Emit(sink, initial);
                    }

                    break;
                }

                case "content_block_delta":
                {
                    int index = Int(e.Data, "index");
                    if (!blocks.TryGetValue(index, out BlockBuilder? builder))
                    {
                        break;
                    }

                    JsonNode? delta = e.Data["delta"];
                    switch ((string?)delta?["type"])
                    {
                        case "text_delta":
                            string piece = (string?)delta!["text"] ?? string.Empty;
                            if (builder.Text.Length == 0 && _textInTurn && piece.Length > 0)
                            {
                                sink.TextDelta("\n\n");
                            }

                            builder.Text.Append(piece);
                            Emit(sink, piece);
                            break;
                        case "input_json_delta":
                            builder.Json.Append((string?)delta!["partial_json"]);
                            break;
                        default:
                            break;
                    }

                    break;
                }

                case "message_delta":
                    stop = (string?)e.Data["delta"]?["stop_reason"] ?? stop;
                    usage = usage.Add(new Usage(0, Int(e.Data["usage"], "output_tokens"), 0, 0));
                    break;

                default:
                    break;
            }
        }

        TotalUsage = TotalUsage.Add(usage);
        sink.UsageUpdated(TotalUsage);

        var content = new JsonArray();
        foreach (BlockBuilder b in blocks.Values)
        {
            if (b.Type == "text" && b.Text.Length > 0)
            {
                content.Add(new JsonObject { ["type"] = "text", ["text"] = b.Text.ToString() });
            }
            else if (b.Type == "tool_use")
            {
                JsonNode input;
                try
                {
                    input = b.Json.Length == 0 ? new JsonObject() : JsonNode.Parse(b.Json.ToString()) ?? new JsonObject();
                }
                catch (JsonException)
                {
                    input = new JsonObject();
                }

                content.Add(new JsonObject { ["type"] = "tool_use", ["id"] = b.Id, ["name"] = b.Name, ["input"] = input });
            }
        }

        if (content.Count == 0)
        {
            content.Add(new JsonObject { ["type"] = "text", ["text"] = "(empty answer)" });
        }

        return (content, stop);
    }

    private JsonObject BuildRequest()
    {
        var toolArray = new JsonArray();
        IReadOnlyList<ToolDefinition> defs = tools.Definitions;
        for (int i = 0; i < defs.Count; i++)
        {
            var t = new JsonObject
            {
                ["name"] = defs[i].Name,
                ["description"] = defs[i].Description,
                ["input_schema"] = defs[i].InputSchema.DeepClone(),
            };

            if (i == defs.Count - 1)
            {
                t["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
            }

            toolArray.Add(t);
        }

        // The history is cloned per request: cache_control goes on the newest message only, and
        // the stored history stays clean for the next turn.
        var messages = new JsonArray(_messages.Select(m => m.DeepClone()).ToArray());
        if (messages.Count > 0 && messages[^1]?["content"] is JsonArray last && last.Count > 0 && last[^1] is JsonObject lastBlock)
        {
            lastBlock["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
        }

        return new JsonObject
        {
            ["model"] = Options.Model,
            ["max_tokens"] = Options.MaxTokens,
            ["system"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = Options.SystemPrompt,
                ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
            }),
            ["tools"] = toolArray,
            ["messages"] = messages,
        };
    }

    private void Emit(IAssistantSink sink, string text)
    {
        if (text.Length > 0)
        {
            _textInTurn = true;
            sink.TextDelta(text);
        }
    }

    private static int Int(JsonNode? node, string name) =>
        node?[name] is JsonValue v && v.TryGetValue(out int n) ? n : 0;

    private sealed class BlockBuilder(string type, string? id, string? name)
    {
        public string Type { get; } = type;

        public string? Id { get; } = id;

        public string? Name { get; } = name;

        public StringBuilder Text { get; } = new();

        public StringBuilder Json { get; } = new();
    }
}

/// <summary>Where a turn's progress goes: the chat panel, or a test.</summary>
public interface IAssistantSink
{
    void TextDelta(string text);

    void ToolStarted(string id, string name, JsonElement input);

    void ToolFinished(string id, string name, ToolResult result);

    void UsageUpdated(Usage total);

    /// <summary>Something the person should know that is not Claude talking - a cut-off answer, a round limit.</summary>
    void Notice(string text);
}

/// <summary>Which model, how long an answer may be, and what it is told it is.</summary>
public sealed record AssistantOptions
{
    /// <summary>Sonnet by default: quick and a fraction of Opus's price, and plenty for reading ladder.</summary>
    public const string DefaultModel = "claude-sonnet-5-5";

    /// <summary>The models offered in the panel, cheapest-sensible first.</summary>
    public static IReadOnlyList<string> Models { get; } = ["claude-sonnet-5-5", "claude-opus-5-5", "claude-haiku-4-5-20251001"];

    public string Model { get; init; } = DefaultModel;

    public int MaxTokens { get; init; } = 8000;

    public string SystemPrompt { get; init; } = AssistantPrompt.System;
}
