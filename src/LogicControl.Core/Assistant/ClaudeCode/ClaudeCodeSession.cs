using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LogicControl.Core.Assistant;

/// <summary>How to run the user's Claude Code for the assistant panel.</summary>
public sealed record ClaudeCodeOptions
{
    /// <summary>The <c>claude</c> executable - see <see cref="ClaudeCodeLocator"/>.</summary>
    public required string Executable { get; init; }

    /// <summary>
    /// The MCP configuration file naming LogicControl's tool server - in the app,
    /// <c>LogicControl.exe --mcp --attach &lt;pipe&gt;</c>, which runs each tool in the open window.
    /// </summary>
    public required string McpConfigFile { get; init; }

    /// <summary>A file holding the system prompt - <see cref="AssistantPrompt.System"/>.</summary>
    public required string SystemPromptFile { get; init; }

    /// <summary>
    /// Where it runs. A folder of LogicControl's own, so no project's CLAUDE.md, hooks or settings
    /// are picked up from wherever the app was started.
    /// </summary>
    public required string WorkingDirectory { get; init; }

    public string Model { get; init; } = AssistantOptions.DefaultModel;
}

/// <summary>
/// The assistant panel driven by Claude Code instead of the API: the user's own <c>claude</c>,
/// signed in with their Claude plan, so the chat uses the plan's usage and needs no API key or
/// credits.
///
/// <para><b>How.</b> One <c>claude -p</c> process per chat, kept running, fed one JSON message per
/// question on stdin and read as a stream of JSON events on stdout (the Agent SDK's streaming
/// mode). Its built-in tools - files, shell, web - are switched off; the only tools it has are
/// LogicControl's, through an MCP server that is LogicControl itself in <c>--attach</c> mode,
/// which hands each call to the open window over a named pipe (<see cref="ToolBridgeServer"/>).
/// So Claude reads the project on screen and its drafts land in the Develop tab as it works,
/// exactly as with an API key.</para>
///
/// <para><b>Stopping</b> kills the process; the next question starts a new one that resumes the
/// same Claude Code session by id, so the history survives. A model change does the same.</para>
/// </summary>
public sealed class ClaudeCodeSession(ClaudeCodeOptions options, IClaudeCodeLauncher? launcher = null) : IConversation, IDisposable
{
    /// <summary>The MCP server's name in the config file; Claude Code prefixes its tools with mcp__logiccontrol__.</summary>
    public const string ServerName = "logiccontrol";

    private const string ToolPrefix = "mcp__" + ServerName + "__";

    private readonly IClaudeCodeLauncher _launcher = launcher ?? ClaudeCodeLauncher.Instance;
    private IClaudeCodeProcess? _process;
    private string? _sessionId;
    private string? _processModel;
    private int _messages;

    public ClaudeCodeOptions Options { get; private set; } = options;

    public string Model => Options.Model;

    public Usage TotalUsage { get; private set; } = Usage.Zero;

    public int MessageCount => _messages;

    /// <summary>Claude Code's id for this chat, once it has started - what a restart resumes.</summary>
    public string? SessionId => _sessionId;

    /// <summary>Switches model; the next question restarts Claude Code on the same session.</summary>
    public void SetModel(string model) => Options = Options with { Model = model };

    public void Reset()
    {
        StopProcess();
        _sessionId = null;
        _messages = 0;
        TotalUsage = Usage.Zero;
    }

    public void Dispose() => StopProcess();

    /// <summary>The command line, for a fresh session or a resumed one.</summary>
    public static IReadOnlyList<string> BuildArguments(ClaudeCodeOptions options, string? resume)
    {
        ArgumentNullException.ThrowIfNull(options);
        var args = new List<string>
        {
            "-p",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--verbose",
            "--include-partial-messages",
            "--model", options.Model,
            "--system-prompt-file", options.SystemPromptFile,
            "--mcp-config", options.McpConfigFile,
            "--strict-mcp-config",

            // No files, no shell, no web: LogicControl's tools are the only ones, and they run
            // without a prompt because nobody is at a terminal to answer one.
            "--tools", string.Empty,
            "--allowedTools", "mcp__" + ServerName,
        };

        if (resume is not null)
        {
            args.Add("--resume");
            args.Add(resume);
        }

        return args;
    }

    public async Task RunTurnAsync(string userText, string? context, IAssistantSink sink, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userText);
        ArgumentNullException.ThrowIfNull(sink);

        if (_process is { HasExited: true } || (_process is not null && _processModel != Options.Model))
        {
            StopProcess();
        }

        if (_process is null)
        {
            try
            {
                Directory.CreateDirectory(Options.WorkingDirectory);
                _process = _launcher.Start(Options.Executable, BuildArguments(Options, _sessionId), Options.WorkingDirectory);
                _processModel = Options.Model;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                throw new ClaudeApiException($"Could not start Claude Code ({Options.Executable}): {ex.Message}", ex);
            }
        }

        string text = string.IsNullOrWhiteSpace(context) ? userText : $"<context>\n{context.Trim()}\n</context>\n\n{userText}";
        var message = new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = text },
        };

        IClaudeCodeProcess process = _process;
        using CancellationTokenRegistration stop = cancellationToken.Register(() => process.Kill());

        try
        {
            await process.Input.WriteLineAsync(message.ToJsonString()).ConfigureAwait(true);
            await process.Input.FlushAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (IOException ex)
        {
            StopProcess();
            cancellationToken.ThrowIfCancellationRequested();
            throw new ClaudeApiException(Explain(process, $"Claude Code stopped before the question reached it ({ex.Message})."), ex);
        }

        _messages++;
        var turn = new TurnState();

        while (true)
        {
            string? line;
            try
            {
                line = await process.Output.ReadLineAsync(CancellationToken.None).ConfigureAwait(true);
            }
            catch (IOException)
            {
                line = null;
            }

            if (line is null)
            {
                StopProcess();
                cancellationToken.ThrowIfCancellationRequested();
                throw new ClaudeApiException(Explain(process, "Claude Code stopped without answering."));
            }

            if (Handle(line, sink, turn) is { } finished)
            {
                _messages++;
                if (finished.IsError)
                {
                    throw new ClaudeApiException(finished.Message);
                }

                return;
            }
        }
    }

    // ------------------------------------------------------------------ the event stream

    private sealed class TurnState
    {
        /// <summary>Text has been shown in this turn - a new text block starts a new paragraph.</summary>
        public bool TextShown { get; set; }

        /// <summary>The message being streamed has shown its text through deltas already.</summary>
        public bool Streamed { get; set; }

        /// <summary>A text block has started in the message being streamed and shown nothing yet.</summary>
        public bool NewBlock { get; set; }

        public Dictionary<string, string> ToolNames { get; } = [];
    }

    private sealed record Finished(bool IsError, string Message);

    /// <summary>One line of Claude Code's output. Returns non-null at the end of the turn.</summary>
    private Finished? Handle(string line, IAssistantSink sink, TurnState turn)
    {
        JsonObject? e;
        try
        {
            e = JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            return null; // Not ours - a stray line is not worth failing a turn for.
        }

        if (e is null)
        {
            return null;
        }

        switch ((string?)e["type"])
        {
            case "system" when (string?)e["subtype"] == "init":
                _sessionId = (string?)e["session_id"] ?? _sessionId;
                if (e["mcp_servers"] is JsonArray servers
                    && servers.OfType<JsonObject>().FirstOrDefault(s => (string?)s["name"] == ServerName) is { } ours
                    && (string?)ours["status"] is { } status && status != "connected")
                {
                    sink.Notice($"LogicControl's tools did not connect to Claude Code ({status}), so it cannot see the project. Try a new chat.");
                }

                break;

            case "stream_event":
                HandleStreamEvent(e["event"] as JsonObject, sink, turn);
                break;

            case "assistant":
                HandleAssistant(e["message"] as JsonObject, sink, turn);
                break;

            case "user":
                HandleToolResults(e["message"] as JsonObject, sink, turn);
                break;

            case "result":
                _sessionId = (string?)e["session_id"] ?? _sessionId;
                TotalUsage = TotalUsage.Add(UsageOf(e["usage"]));
                sink.UsageUpdated(TotalUsage);

                bool isError = e["is_error"] is JsonValue v && v.TryGetValue(out bool b) && b;
                string subtype = (string?)e["subtype"] ?? "success";
                if (isError || subtype != "success")
                {
                    string why = (string?)e["result"] is { Length: > 0 } r ? r : subtype.Replace('_', ' ');
                    return new Finished(true, $"Claude Code: {why}");
                }

                return new Finished(false, string.Empty);

            default:
                break;
        }

        return null;
    }

    private static void HandleStreamEvent(JsonObject? ev, IAssistantSink sink, TurnState turn)
    {
        if (ev is null)
        {
            return;
        }

        string? type = (string?)ev["type"];
        if (type == "message_start")
        {
            turn.Streamed = false;
        }
        else if (type == "content_block_start" && ev["content_block"] is JsonObject block && (string?)block["type"] == "text")
        {
            turn.NewBlock = true;
        }
        else if (type == "content_block_delta" && ev["delta"] is JsonObject delta && (string?)delta["type"] == "text_delta"
            && (string?)delta["text"] is { Length: > 0 } piece)
        {
            if (turn.NewBlock && turn.TextShown)
            {
                sink.TextDelta("\n\n");
            }

            turn.NewBlock = false;
            turn.TextShown = true;
            turn.Streamed = true;
            sink.TextDelta(piece);
        }
    }

    private static void HandleAssistant(JsonObject? message, IAssistantSink sink, TurnState turn)
    {
        if (message?["content"] is not JsonArray content)
        {
            return;
        }

        foreach (JsonObject block in content.OfType<JsonObject>())
        {
            switch ((string?)block["type"])
            {
                case "text" when !turn.Streamed:
                    // No partial messages (an older Claude Code): show the text whole.
                    if ((string?)block["text"] is { Length: > 0 } text)
                    {
                        if (turn.TextShown)
                        {
                            sink.TextDelta("\n\n");
                        }

                        turn.TextShown = true;
                        sink.TextDelta(text);
                    }

                    break;

                case "tool_use":
                {
                    string id = (string?)block["id"] ?? Guid.NewGuid().ToString("N");
                    string name = ToolName((string?)block["name"]);
                    turn.ToolNames[id] = name;
                    sink.ToolStarted(id, name, JsonSerializer.SerializeToElement(block["input"] ?? new JsonObject()));
                    break;
                }

                default:
                    break;
            }
        }

        turn.Streamed = false;
    }

    private static void HandleToolResults(JsonObject? message, IAssistantSink sink, TurnState turn)
    {
        if (message?["content"] is not JsonArray content)
        {
            return;
        }

        foreach (JsonObject block in content.OfType<JsonObject>().Where(b => (string?)b["type"] == "tool_result"))
        {
            string id = (string?)block["tool_use_id"] ?? string.Empty;
            string text = block["content"] switch
            {
                JsonValue v when v.TryGetValue(out string? s) => s ?? string.Empty,
                JsonArray parts => string.Join("\n", parts.OfType<JsonObject>().Select(p => (string?)p["text"]).Where(t => t is not null)),
                _ => string.Empty,
            };
            bool isError = block["is_error"] is JsonValue e && e.TryGetValue(out bool b) && b;
            string name = turn.ToolNames.TryGetValue(id, out string? n) ? n : "tool";
            sink.ToolFinished(id, name, new ToolResult(text, isError, ChangedDrafts: name.StartsWith("draft_", StringComparison.Ordinal)));
        }
    }

    /// <summary>"mcp__logiccontrol__read_routine" is "read_routine" - the name the panel describes.</summary>
    internal static string ToolName(string? name) =>
        name is null ? "tool" : name.StartsWith(ToolPrefix, StringComparison.Ordinal) ? name[ToolPrefix.Length..] : name;

    private static Usage UsageOf(JsonNode? u) => u is null ? Usage.Zero : new Usage(
        Int(u, "input_tokens"), Int(u, "output_tokens"), Int(u, "cache_read_input_tokens"), Int(u, "cache_creation_input_tokens"));

    private static int Int(JsonNode node, string name) =>
        node[name] is JsonValue v && (v.TryGetValue(out int n) || (v.TryGetValue(out double d) && (n = (int)d) >= 0)) ? n : 0;

    private static string Explain(IClaudeCodeProcess process, string what)
    {
        string errors = process.ErrorText.Trim();
        string code = process.ExitCode is { } c ? $" (exit code {c.ToString(CultureInfo.InvariantCulture)})" : string.Empty;
        if (errors.Contains("login", StringComparison.OrdinalIgnoreCase) || errors.Contains("auth", StringComparison.OrdinalIgnoreCase))
        {
            return $"{what}{code} It looks like Claude Code is not signed in - use Sign in to Claude Code in this panel. {errors}";
        }

        return errors.Length > 0 ? $"{what}{code} {errors}" : what + code;
    }

    private void StopProcess()
    {
        _process?.Dispose();
        _process = null;
        _processModel = null;
    }
}
