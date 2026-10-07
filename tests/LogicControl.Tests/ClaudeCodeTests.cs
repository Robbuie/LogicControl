using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using LogicControl.Core.Assistant;
using Xunit;

namespace LogicControl.Tests;

/// <summary>The assistant driven by Claude Code: the event stream, restarts, the locator and the pipe bridge.</summary>
public class ClaudeCodeTests
{
    private static ClaudeCodeOptions Options(string model = "claude-sonnet-5-5") => new()
    {
        Executable = "claude",
        McpConfigFile = "mcp.json",
        SystemPromptFile = "prompt.txt",
        WorkingDirectory = Path.Combine(Path.GetTempPath(), "lc-claude-test"),
        Model = model,
    };

    /// <summary>A claude that answers each question with the lines a script gives for it.</summary>
    private sealed class FakeClaude : IClaudeCodeLauncher
    {
        public List<IReadOnlyList<string>> Starts { get; } = [];

        public List<string> Questions { get; } = [];

        public Func<string, int, IEnumerable<string>> Script { get; set; } = (_, _) => [];

        public FakeProcess? Last { get; private set; }

        public IClaudeCodeProcess Start(string executable, IReadOnlyList<string> arguments, string workingDirectory)
        {
            Starts.Add(arguments);
            Last = new FakeProcess(this);
            return Last;
        }

        public sealed class FakeProcess : IClaudeCodeProcess
        {
            private readonly BlockingCollection<string?> _out = [];

            public FakeProcess(FakeClaude owner)
            {
                Input = new Writer(line =>
                {
                    string text = (string)JsonNode.Parse(line)!["message"]!["content"]!;
                    owner.Questions.Add(text);
                    foreach (string reply in owner.Script(text, owner.Questions.Count))
                    {
                        _out.Add(reply);
                    }
                });
                Output = new Reader(_out);
            }

            public TextWriter Input { get; }

            public TextReader Output { get; }

            public string ErrorText { get; set; } = string.Empty;

            public bool HasExited { get; private set; }

            public int? ExitCode => HasExited ? 1 : null;

            public bool Killed { get; private set; }

            public void Exit() => _out.Add(null);

            public void Kill()
            {
                Killed = true;
                HasExited = true;
                _out.Add(null);
            }

            public void Dispose() => Kill();
        }

        private sealed class Writer(Action<string> onLine) : StringWriter
        {
            public override void WriteLine(string? value) => onLine(value ?? string.Empty);

            public override Task WriteLineAsync(string? value)
            {
                onLine(value ?? string.Empty);
                return Task.CompletedTask;
            }
        }

        private sealed class Reader(BlockingCollection<string?> lines) : TextReader
        {
            public override string? ReadLine() => lines.Take();

            public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
                new(Task.Run(() => lines.Take(), CancellationToken.None));
        }
    }

    private sealed class Sink : IAssistantSink
    {
        public System.Text.StringBuilder Text { get; } = new();

        public List<string> Tools { get; } = [];

        public List<string> Notices { get; } = [];

        public Usage? Usage { get; private set; }

        public void TextDelta(string text) => Text.Append(text);

        public void ToolStarted(string id, string name, JsonElement input) => Tools.Add($"start {name} {input.GetRawText()}");

        public void ToolFinished(string id, string name, ToolResult result) => Tools.Add($"done {name} {(result.IsError ? "error" : "ok")} {result.Text}");

        public void UsageUpdated(Usage total) => Usage = total;

        public void Notice(string text) => Notices.Add(text);
    }

    private static string Init(string session = "s-1", string status = "connected") =>
        $$"""{"type":"system","subtype":"init","session_id":"{{session}}","mcp_servers":[{"name":"logiccontrol","status":"{{status}}"}]}""";

    private static string Delta(string text) =>
        new JsonObject { ["type"] = "stream_event", ["event"] = new JsonObject { ["type"] = "content_block_delta", ["index"] = 0, ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = text } } }.ToJsonString();

    private const string BlockStart = """{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}}""";

    private const string Result = """{"type":"result","subtype":"success","is_error":false,"result":"done","session_id":"s-1","usage":{"input_tokens":100,"output_tokens":20,"cache_read_input_tokens":50,"cache_creation_input_tokens":0}}""";

    [Fact]
    public void TheCommandLineGivesClaudeOnlyLogicControlsTools()
    {
        IReadOnlyList<string> args = ClaudeCodeSession.BuildArguments(Options(), resume: null);

        Assert.Equal("-p", args[0]);
        Assert.Equal("stream-json", args[args.ToList().IndexOf("--input-format") + 1]);
        Assert.Equal("stream-json", args[args.ToList().IndexOf("--output-format") + 1]);
        Assert.Contains("--include-partial-messages", args);
        Assert.Contains("--strict-mcp-config", args);
        Assert.Equal(string.Empty, args[args.ToList().IndexOf("--tools") + 1]);
        Assert.Equal("mcp__logiccontrol", args[args.ToList().IndexOf("--allowedTools") + 1]);
        Assert.Equal("claude-sonnet-5-5", args[args.ToList().IndexOf("--model") + 1]);
        Assert.DoesNotContain("--resume", args);

        IReadOnlyList<string> resumed = ClaudeCodeSession.BuildArguments(Options(), resume: "abc");
        Assert.Equal("abc", resumed[^1]);
        Assert.Equal("--resume", resumed[^2]);
    }

    [Fact]
    public async Task ATurnStreamsTextAndToolCalls()
    {
        var claude = new FakeClaude
        {
            Script = (_, _) =>
            [
                Init(),
                """{"type":"stream_event","event":{"type":"message_start"}}""",
                BlockStart,
                Delta("Let me "),
                Delta("look."),
                """{"type":"assistant","message":{"content":[{"type":"text","text":"Let me look."},{"type":"tool_use","id":"t1","name":"mcp__logiccontrol__read_routine","input":{"program":"MainProgram","routine":"Motors"}}]}}""",
                """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":[{"type":"text","text":"7 rungs"}]}]}}""",
                """{"type":"stream_event","event":{"type":"message_start"}}""",
                BlockStart,
                Delta("Rung 3 writes it."),
                """{"type":"assistant","message":{"content":[{"type":"text","text":"Rung 3 writes it."}]}}""",
                Result,
            ],
        };

        using var session = new ClaudeCodeSession(Options(), claude);
        var sink = new Sink();
        await session.RunTurnAsync("Why does M101 not start?", "Tab: Logic", sink);

        Assert.Equal("Let me look.\n\nRung 3 writes it.", sink.Text.ToString());
        Assert.Equal(
            new[] { """start read_routine {"program":"MainProgram","routine":"Motors"}""", "done read_routine ok 7 rungs" },
            sink.Tools);
        Assert.Equal("s-1", session.SessionId);
        Assert.Equal(150, sink.Usage!.InputTokens + sink.Usage.CacheReadTokens);
        Assert.StartsWith("<context>\nTab: Logic\n</context>", claude.Questions[0], StringComparison.Ordinal);
        Assert.Equal(2, session.MessageCount);
    }

    [Fact]
    public async Task WithoutPartialMessagesTheWholeTextIsShown()
    {
        var claude = new FakeClaude
        {
            Script = (_, _) =>
            [
                Init(),
                """{"type":"assistant","message":{"content":[{"type":"text","text":"Hello."}]}}""",
                """{"type":"assistant","message":{"content":[{"type":"text","text":"Again."}]}}""",
                Result,
            ],
        };

        using var session = new ClaudeCodeSession(Options(), claude);
        var sink = new Sink();
        await session.RunTurnAsync("hi", null, sink);
        Assert.Equal("Hello.\n\nAgain.", sink.Text.ToString());
    }

    [Fact]
    public async Task OneProcessServesTheWholeChatUntilTheModelChanges()
    {
        var claude = new FakeClaude { Script = (_, n) => [Init(), Delta($"answer {n}"), Result] };
        using var session = new ClaudeCodeSession(Options(), claude);

        await session.RunTurnAsync("one", null, new Sink());
        await session.RunTurnAsync("two", null, new Sink());
        Assert.Single(claude.Starts);

        session.SetModel("claude-opus-5-5");
        await session.RunTurnAsync("three", null, new Sink());

        Assert.Equal(2, claude.Starts.Count);
        Assert.Equal("claude-opus-5-5", claude.Starts[1][claude.Starts[1].ToList().IndexOf("--model") + 1]);
        Assert.Equal("s-1", claude.Starts[1][^1]); // resumed, so the history carries over
    }

    [Fact]
    public async Task StoppingKillsItAndTheNextQuestionResumes()
    {
        var claude = new FakeClaude { Script = (q, _) => q == "slow" ? [Init(), Delta("thinking...")] : [Init(), Delta("ok"), Result] };
        using var session = new ClaudeCodeSession(Options(), claude);
        using var cts = new CancellationTokenSource();

        Task turn = session.RunTurnAsync("slow", null, new Sink(), cts.Token);
        await Task.Delay(50);
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => turn);
        Assert.True(claude.Last!.Killed);

        var sink = new Sink();
        await session.RunTurnAsync("next", null, sink);
        Assert.Equal(2, claude.Starts.Count);
        Assert.Equal("--resume", claude.Starts[1][^2]);
        Assert.Equal("ok", sink.Text.ToString());
    }

    [Fact]
    public async Task ErrorsAndASignedOutClaudeAreExplained()
    {
        var claude = new FakeClaude
        {
            Script = (_, _) => [Init(), """{"type":"result","subtype":"success","is_error":true,"result":"Invalid API key · Please run /login"}"""],
        };
        using var session = new ClaudeCodeSession(Options(), claude);
        ClaudeApiException error = await Assert.ThrowsAsync<ClaudeApiException>(() => session.RunTurnAsync("hi", null, new Sink()));
        Assert.Contains("/login", error.Message, StringComparison.Ordinal);

        var dies = new FakeClaude();
        dies.Script = (_, _) =>
        {
            dies.Last!.ErrorText = "Not logged in. Run claude auth login.";
            dies.Last.Exit();
            return [];
        };
        using var gone = new ClaudeCodeSession(Options(), dies);
        ClaudeApiException stopped = await Assert.ThrowsAsync<ClaudeApiException>(() => gone.RunTurnAsync("hi", null, new Sink()));
        Assert.Contains("not signed in", stopped.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolsThatDidNotConnectAreReported()
    {
        var claude = new FakeClaude { Script = (_, _) => [Init(status: "failed"), Result] };
        using var session = new ClaudeCodeSession(Options(), claude);
        var sink = new Sink();
        await session.RunTurnAsync("hi", null, sink);
        Assert.Contains("did not connect", Assert.Single(sink.Notices), StringComparison.Ordinal);
    }

    [Fact]
    public void TheLocatorLooksInTheUsualPlacesInOrder()
    {
        string home = OperatingSystem.IsWindows() ? @"C:\Users\me" : "/home/me";
        var env = new Dictionary<string, string?>
        {
            [OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME"] = home,
            ["PATH"] = string.Join(Path.PathSeparator, Path.Combine(home, "bin1"), Path.Combine(home, "bin2")),
            ["APPDATA"] = Path.Combine(home, "AppData"),
        };
        string extensions = Path.Combine(home, ".vscode", "extensions");
        var dirs = new Dictionary<string, string[]>
        {
            [extensions] = [Path.Combine(extensions, "anthropic.claude-code-2.1.9-win32-x64"), Path.Combine(extensions, "anthropic.claude-code-2.1.10-win32-x64"), Path.Combine(extensions, "ms-python.python-1")],
        };
        string exe = OperatingSystem.IsWindows() ? "claude.exe" : "claude";
        var files = new HashSet<string>();
        var locator = new ClaudeCodeLocator(files.Contains, d => dirs.TryGetValue(d, out string[]? v) ? v : [], k => env.GetValueOrDefault(k));

        Assert.Null(locator.Find());

        string newest = Path.Combine(extensions, "anthropic.claude-code-2.1.10-win32-x64", "resources", "native-binary", exe);
        files.Add(newest);
        files.Add(Path.Combine(extensions, "anthropic.claude-code-2.1.9-win32-x64", "resources", "native-binary", exe));
        Assert.Equal(newest, locator.Find());

        string onPath = Path.Combine(home, "bin2", exe);
        files.Add(onPath);
        Assert.Equal(onPath, locator.Find());

        string native = Path.Combine(home, ".local", "bin", exe);
        files.Add(native);
        Assert.Equal(native, locator.Find());

        files.Add("/custom/claude");
        Assert.Equal("/custom/claude", locator.Find("\"/custom/claude\""));
    }

    [Fact]
    public async Task TheBridgeCarriesACallToTheWindowAndBack()
    {
        string pipe = ToolBridgeServer.NewPipeName();
        var calls = new List<string>();
        using var server = new ToolBridgeServer(pipe, (name, input) =>
        {
            calls.Add($"{name} {input.GetRawText()}");
            return Task.FromResult(name == "boom" ? ToolResult.Fail("no such tool") : ToolResult.Ok("3 routines"));
        });

        var client = new ToolBridgeClient(pipe);
        ToolResult ok = await Task.Run(() => client.Execute("list_routines", JsonSerializer.SerializeToElement(new { program = "MainProgram" })));
        ToolResult bad = await Task.Run(() => client.Execute("boom", JsonSerializer.SerializeToElement(new { })));

        Assert.Equal("3 routines", ok.Text);
        Assert.False(ok.IsError);
        Assert.True(bad.IsError);
        Assert.Equal("""list_routines {"program":"MainProgram"}""", calls[0]);

        ToolResult nobody = new ToolBridgeClient(ToolBridgeServer.NewPipeName()).Execute("list_routines", JsonSerializer.SerializeToElement(new { }));
        Assert.True(nobody.IsError);
        Assert.Contains("LogicControl window", nobody.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAttachedMcpServerListsTheToolsAndForwardsCalls()
    {
        var forwarded = new List<string>();
        var server = new McpServer(LogicTools.DefinitionsFor(canOpenProjects: false), (name, _) =>
        {
            forwarded.Add(name);
            return ToolResult.Ok("overview");
        }, "test");

        JsonObject list = server.Handle("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        JsonArray tools = (JsonArray)list["result"]!["tools"]!;
        Assert.Contains(tools, t => (string?)t!["name"] == "draft_routine");
        Assert.DoesNotContain(tools, t => (string?)t!["name"] == "open_project");

        JsonObject call = server.Handle("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"project_overview","arguments":{}}}""")!;
        Assert.Equal("overview", (string?)call["result"]!["content"]![0]!["text"]);
        Assert.Equal(new[] { "project_overview" }, forwarded);
    }
}
