using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LogicControl.App.ViewModels;
using LogicControl.App.ViewModels.Assistant;
using LogicControl.Core.Analysis;
using LogicControl.Core.Assistant;
using LogicControl.Core.Authoring;
using Xunit;

namespace LogicControl.Tests;

public class AssistantTests
{
    /// <summary>A host over the Line3 fixture and a fresh development set.</summary>
    private sealed class Host(bool withProject = true) : IToolHost
    {
        public ProjectAnalysis? Analysis { get; private set; } = withProject ? Fixture.Line3Analysed : null;

        public DevelopmentSet Drafts { get; } = new();

        public bool CanOpenProjects => true;

        public int Changes { get; private set; }

        public string? OpenProject(string path)
        {
            if (!File.Exists(path))
            {
                return $"No file at {path}.";
            }

            Analysis = ProjectAnalysis.Open(path);
            return null;
        }

        public void DraftsChanged(string summary) => Changes++;
    }

    private static JsonElement Input(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void EveryToolHasASchemaTheApiAccepts()
    {
        var tools = new LogicTools(new Host());

        Assert.Equal(tools.Definitions.Count, tools.Definitions.Select(d => d.Name).Distinct().Count());
        Assert.All(tools.Definitions, d =>
        {
            Assert.Matches("^[a-z_]{1,64}$", d.Name);
            Assert.Equal("object", (string?)d.InputSchema["type"]);
            Assert.True(d.Description.Length > 20, d.Name);
        });
    }

    [Fact]
    public void ReadToolsAnswerFromTheOpenProject()
    {
        var host = new Host();
        var tools = new LogicTools(host);
        string program = host.Analysis!.Project.Programs[0].Name;
        string routine = host.Analysis.Project.Programs[0].Routines[0].Name;

        ToolResult overview = tools.Execute("project_overview", Input(new { }));
        Assert.False(overview.IsError);
        Assert.Contains(host.Analysis.Project.Controller.Name, overview.Text, StringComparison.Ordinal);
        Assert.Contains("Tasks:", overview.Text, StringComparison.Ordinal);

        ToolResult read = tools.Execute("read_routine", Input(new { program, routine }));
        Assert.False(read.IsError, read.Text);
        Assert.Contains("   0: ", read.Text, StringComparison.Ordinal);

        string tag = host.Analysis.Project.Tags.First(t => host.Analysis.CrossReference.UsesOf(t).Count > 0).Name;
        ToolResult refs = tools.Execute("tag_references", Input(new { tag }));
        Assert.False(refs.IsError, refs.Text);
        Assert.Contains(" :: ", refs.Text, StringComparison.Ordinal);

        Assert.False(tools.Execute("list_findings", Input(new { })).IsError);
        Assert.False(tools.Execute("list_communications", Input(new { })).IsError);
        Assert.False(tools.Execute("list_hardware", Input(new { })).IsError);
        Assert.Contains("Timer, Preset, Accum", tools.Execute("instruction_help", Input(new { mnemonic = "ton" })).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void MistakesComeBackAsErrorsTheModelCanRead()
    {
        var tools = new LogicTools(new Host());

        ToolResult missing = tools.Execute("read_routine", Input(new { program = "Nope", routine = "Main" }));
        Assert.True(missing.IsError);
        Assert.Contains("list_routines", missing.Text, StringComparison.Ordinal);

        Assert.True(tools.Execute("read_routine", Input(new { program = "x" })).IsError);
        Assert.True(tools.Execute("no_such_tool", Input(new { })).IsError);
        Assert.Contains("open_project", new LogicTools(new Host(withProject: false)).Execute("project_overview", Input(new { })).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void DraftToolsWriteIntoTheSetAndReportTheChecks()
    {
        var host = new Host();
        var tools = new LogicTools(host);
        string program = host.Analysis!.Project.Programs[0].Name;

        ToolResult udt = tools.Execute("draft_data_type", Input(new
        {
            name = "Conveyor",
            members = new object[] { new { name = "Run", data_type = "bool" }, new { name = "Jam", data_type = "TIMER" } },
        }));
        Assert.True(udt.ChangedDrafts);
        Assert.Contains("no problems", udt.Text, StringComparison.Ordinal);

        tools.Execute("draft_tags", Input(new { tags = new object[] { new { name = "CV1", data_type = "Conveyor" } } }));

        ToolResult bad = tools.Execute("draft_routine", Input(new
        {
            program,
            routine = "Conveyors",
            rungs = new object[] { new { text = "XIC(CV1.Run) TON(CV1.Jam,5000)", comment = "jam" } },
        }));
        Assert.Contains("ERROR", bad.Text, StringComparison.Ordinal);
        Assert.Contains("TON at position", bad.Text, StringComparison.Ordinal);

        ToolResult good = tools.Execute("draft_routine", Input(new
        {
            program,
            routine = "Conveyors",
            rungs = new object[] { new { text = "XIC(CV1.Run)TON(CV1.Jam,5000,0);" } },
        }));
        Assert.Contains("no problems", good.Text, StringComparison.Ordinal);

        Assert.Single(host.Drafts.DataTypes);
        Assert.Single(host.Drafts.Tags);
        Assert.Equal("XIC(CV1.Run)TON(CV1.Jam,5000,0);", host.Drafts.Routines.Single().Rungs.Single().Text);
        Assert.Equal(4, host.Changes);
    }

    [Fact]
    public void AppendingStartsFromTheProjectsRoutine()
    {
        var host = new Host();
        var tools = new LogicTools(host);
        var routine = host.Analysis!.Project.Programs[0].Routines.First(r => r.Rungs.Count > 0);

        tools.Execute("draft_routine", Input(new
        {
            program = routine.Owner,
            routine = routine.Name,
            mode = "append",
            rungs = new object[] { new { text = "NOP();" } },
        }));

        Assert.Equal(routine.Rungs.Count + 1, host.Drafts.Routines.Single().Rungs.Count);
    }

    [Fact]
    public void OpenProjectIsOfferedOnlyWhereTheHostAllowsIt()
    {
        var host = new Host(withProject: false);
        var tools = new LogicTools(host);

        Assert.Contains(tools.Definitions, d => d.Name == "open_project");
        ToolResult opened = tools.Execute("open_project", Input(new { path = Fixture.PathOf("Line3.L5X") }));
        Assert.False(opened.IsError, opened.Text);
        Assert.NotNull(host.Analysis);
    }

    // ------------------------------------------------------------------ the conversation loop

    /// <summary>Answers each request with the next canned event stream, and keeps the bodies.</summary>
    private sealed class ScriptedApi(params string[] streams) : HttpMessageHandler
    {
        private int _next;

        public List<JsonObject> Requests { get; } = [];

        public List<HttpRequestMessage> Raw { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Raw.Add(request);
            Requests.Add((JsonObject)JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!);
            string body = streams[Math.Min(_next++, streams.Length - 1)];
            if (body.StartsWith("HTTP ", StringComparison.Ordinal))
            {
                int status = int.Parse(body[5..8], System.Globalization.CultureInfo.InvariantCulture);
                return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body[9..]) };
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
        }
    }

    private static string Sse(params object[] events) =>
        string.Concat(events.Select(e => $"event: x\ndata: {JsonSerializer.Serialize(e)}\n\n"));

    private static string ToolCall(string id, string name, string json) => Sse(
        new { type = "message_start", message = new { usage = new { input_tokens = 1000, output_tokens = 1, cache_read_input_tokens = 0, cache_creation_input_tokens = 900 } } },
        new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } },
        new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = "Let me look." } },
        new { type = "content_block_stop", index = 0 },
        new { type = "content_block_start", index = 1, content_block = new { type = "tool_use", id, name, input = new { } } },
        new { type = "content_block_delta", index = 1, delta = new { type = "input_json_delta", partial_json = json[..(json.Length / 2)] } },
        new { type = "content_block_delta", index = 1, delta = new { type = "input_json_delta", partial_json = json[(json.Length / 2)..] } },
        new { type = "content_block_stop", index = 1 },
        new { type = "message_delta", delta = new { stop_reason = "tool_use" }, usage = new { output_tokens = 40 } },
        new { type = "message_stop" });

    private static string Answer(string text) => Sse(
        new { type = "message_start", message = new { usage = new { input_tokens = 50, output_tokens = 1, cache_read_input_tokens = 900, cache_creation_input_tokens = 0 } } },
        new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } },
        new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = text[..3] } },
        new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = text[3..] } },
        new { type = "content_block_stop", index = 0 },
        new { type = "message_delta", delta = new { stop_reason = "end_turn" }, usage = new { output_tokens = 20 } },
        new { type = "message_stop" });

    private sealed class Recorder : IAssistantSink
    {
        public StringBuilder Text { get; } = new();

        public List<string> Tools { get; } = [];

        public List<string> Notices { get; } = [];

        public Usage Usage { get; private set; } = Usage.Zero;

        public void TextDelta(string text) => Text.Append(text);

        public void ToolStarted(string id, string name, JsonElement input) => Tools.Add($"start {name}");

        public void ToolFinished(string id, string name, ToolResult result) => Tools.Add($"done {name} {(result.IsError ? "error" : "ok")}");

        public void UsageUpdated(Usage total) => Usage = total;

        public void Notice(string text) => Notices.Add(text);
    }

    [Fact]
    public async Task ATurnRunsToolsAndSendsTheirResultsBack()
    {
        var api = new ScriptedApi(ToolCall("toolu_1", "project_overview", "{}"), Answer("It is Line 3."));
        using var client = new ClaudeClient("sk-ant-test", api);
        var session = new ConversationSession(client, new LogicTools(new Host()), new AssistantOptions());
        var sink = new Recorder();

        await session.RunTurnAsync("What is this?", "Open routine: MainProgram/MainRoutine", sink);

        Assert.Equal("Let me look.\n\nIt is Line 3.", sink.Text.ToString());
        Assert.Equal(new[] { "start project_overview", "done project_overview ok" }, sink.Tools);
        Assert.Equal(2, api.Requests.Count);

        // First request: model, system prompt and tools cached, context ahead of the question.
        JsonObject first = api.Requests[0];
        Assert.Equal(AssistantOptions.DefaultModel, (string?)first["model"]);
        Assert.True((bool)first["stream"]!);
        Assert.NotNull(first["system"]![0]!["cache_control"]);
        Assert.NotNull(first["tools"]!.AsArray()[^1]!["cache_control"]);
        Assert.StartsWith("<context>", (string?)first["messages"]![0]!["content"]![0]!["text"], StringComparison.Ordinal);
        Assert.Equal("sk-ant-test", api.Raw[0].Headers.GetValues("x-api-key").Single());
        Assert.Equal(ClaudeClient.ApiVersion, api.Raw[0].Headers.GetValues("anthropic-version").Single());

        // Second request: the assistant's tool call, then the tool result answering it.
        JsonArray messages = api.Requests[1]["messages"]!.AsArray();
        Assert.Equal(3, messages.Count);
        Assert.Equal("toolu_1", (string?)messages[1]!["content"]![1]!["id"]);
        Assert.Equal("tool_result", (string?)messages[2]!["content"]![0]!["type"]);
        Assert.Equal("toolu_1", (string?)messages[2]!["content"]![0]!["tool_use_id"]);
        Assert.Contains("Tasks:", (string?)messages[2]!["content"]![0]!["content"], StringComparison.Ordinal);

        Assert.Equal(4, session.MessageCount);
        Assert.Equal(62, sink.Usage.OutputTokens);
        Assert.Equal(900, sink.Usage.CacheReadTokens);
    }

    [Fact]
    public async Task ARefusedKeyIsSaidPlainlyAndTheHistoryStaysUsable()
    {
        var api = new ScriptedApi("HTTP 401 {\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"invalid x-api-key\"}}", Answer("Hello there."));
        using var client = new ClaudeClient("sk-ant-bad", api) { RetryDelays = [] };
        var session = new ConversationSession(client, new LogicTools(new Host()), new AssistantOptions());

        ClaudeApiException ex = await Assert.ThrowsAsync<ClaudeApiException>(() => session.RunTurnAsync("hi", null, new Recorder()));
        Assert.Contains("API key was refused", ex.Message, StringComparison.Ordinal);

        // The failed turn is closed off, so the next one is a valid user/assistant/user sequence.
        await session.RunTurnAsync("hi again", null, new Recorder());
        JsonArray roles = new(api.Requests[1]["messages"]!.AsArray().Select(m => (JsonNode?)JsonValue.Create((string?)m!["role"])).ToArray());
        Assert.Equal("[\"user\",\"assistant\",\"user\"]", roles.ToJsonString());
    }

    [Fact]
    public async Task TransientFailuresAreRetried()
    {
        var api = new ScriptedApi("HTTP 529 {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}", Answer("Fine now."));
        using var client = new ClaudeClient("sk-ant-test", api) { RetryDelays = [TimeSpan.Zero, TimeSpan.Zero] };
        var session = new ConversationSession(client, new LogicTools(new Host()), new AssistantOptions());
        var sink = new Recorder();

        await session.RunTurnAsync("hi", null, sink);

        Assert.Equal(2, api.Requests.Count);
        Assert.Equal("Fine now.", sink.Text.ToString());
    }

    // ------------------------------------------------------------------ the panel

    [Fact]
    public async Task ThePanelStreamsTheAnswerAndPutsDraftsInTheDevelopTab()
    {
        var api = new ScriptedApi(
            ToolCall("toolu_1", "draft_data_type", "{\"name\":\"Pump\",\"members\":[{\"name\":\"Run\",\"data_type\":\"BOOL\"}]}"),
            Answer("Drafted ```\nXIC(Start)OTE(Pump1.Run);\n``` for you."));

        var main = new MainViewModel(new MemoryKeyStore("sk-ant-test-0000000000"), () => api);
        await main.OpenAsync(Fixture.PathOf("Line3.L5X"));
        AssistantViewModel chat = main.Assistant;

        Assert.True(chat.HasKey);
        chat.Input = "Make me a pump type";
        Assert.True(chat.SendCommand.CanExecute(null));
        await chat.SendAsync();

        Assert.False(chat.IsBusy);
        Assert.Equal("Pump", Assert.Single(main.Develop.Set.DataTypes).Name);
        Assert.Contains(main.Develop.Items, i => i.Name == "Pump");

        ToolActivityViewModel tool = Assert.Single(chat.Items.OfType<ToolActivityViewModel>());
        Assert.Equal("Drafting data type Pump", tool.Summary);
        Assert.Equal(Level.Info, tool.Level);

        ChatMessageViewModel answer = chat.Items.OfType<ChatMessageViewModel>().Last();
        Assert.Equal(ChatRole.Assistant, answer.Role);
        Assert.Contains(answer.Segments, seg => seg.IsRung && seg.Text == "XIC(Start)OTE(Pump1.Run);");

        // What the user was looking at went with the question.
        string sent = (string)api.Requests[0]["messages"]![0]!["content"]![0]!["text"]!;
        Assert.Contains("Open project:", sent, StringComparison.Ordinal);
    }

    [Fact]
    public void AKeyThatIsNotAnAnthropicKeyIsRefused()
    {
        var chat = new MainViewModel(backend: AssistantBackend.ApiKey).Assistant;

        Assert.True(chat.NeedsKey);
        Assert.NotNull(chat.SaveKey("hello"));
        Assert.Null(chat.SaveKey("sk-ant-api03-abcdefghijklmnop"));
        Assert.True(chat.HasKey);
        chat.ForgetKeyCommand.Execute(null);
        Assert.True(chat.NeedsKey);
    }

    [Fact]
    public void ChatTextSplitsIntoProseCodeAndRungs()
    {
        IReadOnlyList<ChatSegment> parts = ChatSegment.Parse(
            "## Fix\nThe **seal-in** is missing:\n```ladder\n[XIC(Start),XIC(Run)]XIO(Stop)OTE(Run);\nnot a rung\n```\nDone.");

        Assert.Equal(new[] { ChatSegmentKind.Heading, ChatSegmentKind.Text, ChatSegmentKind.Rung, ChatSegmentKind.Code, ChatSegmentKind.Text }, parts.Select(p => p.Kind));
        Assert.Equal("Fix", parts[0].Text);
        Assert.Equal("The seal-in is missing:", parts[1].Text);
        Assert.Equal(new[] { false, true, false }, parts[1].Inlines.Select(i => i.Bold));
        Assert.Equal("not a rung", parts[3].Text);
    }

    [Fact]
    public void ListsTablesAndRungsAnywhereAreStructured()
    {
        IReadOnlyList<ChatSegment> parts = ChatSegment.Parse("""
            Two outputs have more than one writer:

            | Tag | Rungs | Note |
            |-----|:-----:|------|
            | `Conveyor_Run` | Motors 2, 3 | **double coil** |
            | Manual_Mode | OldLogic 0 |

            - Rung 3 overrides rung 2, see `XIC(Manual_Mode)OTE(Conveyor_Run);`
              1. nested step
            Rung 4: XIC(A)OTE(B);

            ```
            // MainProgram/Motors rung 3 - manual jog
            XIC(Manual_Mode)XIO(Line_Running)OTE(Conveyor_Run);
            XIC(X)OTE(Y);
            ```
            """);

        Assert.Equal(
            new[] { ChatSegmentKind.Text, ChatSegmentKind.Table, ChatSegmentKind.Bullet, ChatSegmentKind.Rung, ChatSegmentKind.Bullet,
                    ChatSegmentKind.Rung, ChatSegmentKind.Rung, ChatSegmentKind.Rung },
            parts.Select(p => p.Kind));

        ChatSegment table = parts[1];
        Assert.Equal(new[] { "Tag", "Rungs", "Note" }, table.Rows[0]);
        Assert.Equal(new[] { "Conveyor_Run", "Motors 2, 3", "double coil" }, table.Rows[1]);
        Assert.Equal(new[] { "Manual_Mode", "OldLogic 0", string.Empty }, table.Rows[2]);

        Assert.Equal("•", parts[2].Marker);
        Assert.Contains(parts[2].Inlines, i => i.Code && i.Text == "XIC(Manual_Mode)OTE(Conveyor_Run);");
        Assert.Equal("XIC(Manual_Mode)OTE(Conveyor_Run);", parts[3].Text);
        Assert.Equal("1.", parts[4].Marker);
        Assert.Equal(1, parts[4].Level);

        Assert.Equal("Rung 4", parts[5].Caption);
        Assert.Equal("XIC(A)OTE(B);", parts[5].Text);
        Assert.Equal("MainProgram/Motors rung 3 - manual jog", parts[6].Caption);
        Assert.Null(parts[7].Caption);
    }

}
