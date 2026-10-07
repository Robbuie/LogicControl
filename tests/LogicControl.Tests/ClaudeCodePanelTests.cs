using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using LogicControl.App.ViewModels;
using LogicControl.App.ViewModels.Assistant;
using LogicControl.Core.Assistant;
using Xunit;

namespace LogicControl.Tests;

/// <summary>
/// The assistant panel on the Claude Code back end, end to end: the panel starts "claude", which
/// (faked here) calls a LogicControl tool through the pipe exactly as the --attach MCP server
/// would, and the draft lands in the Develop tab.
/// </summary>
public class ClaudeCodePanelTests
{
    /// <summary>A claude whose answer to each question is worked out by a callback that can use the pipe.</summary>
    private sealed class ScriptedClaude : IClaudeCodeLauncher
    {
        public List<IReadOnlyList<string>> Starts { get; } = [];

        public string? McpConfig { get; private set; }

        public Func<string, string, IEnumerable<string>> Answer { get; set; } = (_, _) => [];

        public IClaudeCodeProcess Start(string executable, IReadOnlyList<string> arguments, string workingDirectory)
        {
            Starts.Add(arguments);
            McpConfig = File.ReadAllText(arguments[arguments.ToList().IndexOf("--mcp-config") + 1]);
            string pipe = (string)JsonNode.Parse(McpConfig)!["mcpServers"]!["logiccontrol"]!["args"]![2]!;
            return new Proc(question => Answer(question, pipe));
        }

        private sealed class Proc : IClaudeCodeProcess
        {
            private readonly BlockingCollection<string?> _out = [];

            public Proc(Func<string, IEnumerable<string>> answer)
            {
                Input = new LineWriter(line =>
                {
                    string q = (string)JsonNode.Parse(line)!["message"]!["content"]!;
                    _ = Task.Run(() =>
                    {
                        foreach (string reply in answer(q))
                        {
                            _out.Add(reply);
                        }
                    });
                });
            }

            public TextWriter Input { get; }

            public TextReader Output => new LineReader(_out);

            public string ErrorText => string.Empty;

            public bool HasExited { get; private set; }

            public int? ExitCode => null;

            public void Kill()
            {
                HasExited = true;
                _out.Add(null);
            }

            public void Dispose() => Kill();
        }

        private sealed class LineWriter(Action<string> onLine) : StringWriter
        {
            public override Task WriteLineAsync(string? value)
            {
                onLine(value ?? string.Empty);
                return Task.CompletedTask;
            }
        }

        private sealed class LineReader(BlockingCollection<string?> lines) : TextReader
        {
            public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
                new(Task.Run(() => lines.Take(), CancellationToken.None));
        }
    }

    private static (MainViewModel Main, ScriptedClaude Claude) Panel(ClaudeCodeState state = ClaudeCodeState.Ready)
    {
        var claude = new ScriptedClaude();
        var env = new ClaudeCodeEnvironment
        {
            Launcher = claude,
            Check = _ => Task.FromResult(new ClaudeCodeStatus(state, state == ClaudeCodeState.NotInstalled ? null : "/fake/claude", "your Claude plan")),
            DataFolder = Path.Combine(Path.GetTempPath(), $"lc-panel-{Guid.NewGuid():N}"),
            SelfExecutable = "/fake/LogicControl.exe",
        };
        return (new MainViewModel(claudeCode: env), claude);
    }

    [Fact]
    public async Task WithNoKeyThePanelUsesClaudeCodeAndSaysWhatIsMissing()
    {
        (MainViewModel main, _) = Panel(ClaudeCodeState.NotInstalled);
        AssistantViewModel chat = main.Assistant;

        Assert.Equal(AssistantBackend.ClaudeCode, chat.Backend);
        Assert.False(chat.NeedsKey);
        await chat.CheckClaudeCodeAsync();
        Assert.True(chat.ClaudeCodeMissing);
        Assert.False(chat.IsReady);
        chat.Input = "hello";
        Assert.False(chat.SendCommand.CanExecute(null));

        (MainViewModel signedOut, _) = Panel(ClaudeCodeState.SignedOut);
        await signedOut.Assistant.CheckClaudeCodeAsync();
        Assert.True(signedOut.Assistant.ClaudeCodeSignedOut);
        Assert.Equal("Claude Code is installed but not signed in.", signedOut.Assistant.BackendNote);

        // Switching to an API key shows the key card instead.
        chat.BackendIndex = 1;
        Assert.Equal(AssistantBackend.ApiKey, chat.Backend);
        Assert.True(chat.NeedsKey);
        Assert.False(chat.ClaudeCodeMissing);
    }

    [Fact]
    public async Task ClaudeCodeDraftsIntoTheOpenWindowThroughThePipe()
    {
        (MainViewModel main, ScriptedClaude claude) = Panel();
        await main.OpenAsync(Fixture.PathOf("Line3.L5X"));
        AssistantViewModel chat = main.Assistant;
        await chat.CheckClaudeCodeAsync();
        Assert.Equal("Claude Code, signed in with your Claude plan.", chat.BackendNote);

        claude.Answer = (question, pipe) =>
        {
            // What the --attach MCP server does for Claude Code: call the window over the pipe.
            ToolResult drafted = new ToolBridgeClient(pipe).Execute("draft_data_type",
                JsonSerializer.SerializeToElement(new { name = "Pump", members = new[] { new { name = "Run", data_type = "BOOL" } } }));

            return
            [
                """{"type":"system","subtype":"init","session_id":"sess-9","mcp_servers":[{"name":"logiccontrol","status":"connected"}]}""",
                """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"mcp__logiccontrol__draft_data_type","input":{"name":"Pump"}}]}}""",
                new JsonObject
                {
                    ["type"] = "user",
                    ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = "t1", ["content"] = drafted.Text, ["is_error"] = drafted.IsError }) },
                }.ToJsonString(),
                """{"type":"assistant","message":{"content":[{"type":"text","text":"Drafted Pump."}]}}""",
                """{"type":"result","subtype":"success","is_error":false,"session_id":"sess-9","usage":{"input_tokens":10,"output_tokens":5}}""",
            ];
        };

        chat.Input = "Make me a pump type";
        Assert.True(chat.SendCommand.CanExecute(null));
        await chat.SendAsync();

        Assert.Equal("Pump", Assert.Single(main.Develop.Set.DataTypes).Name);
        Assert.Equal("Claude", main.Develop.History.Latest!.Author);
        ToolActivityViewModel tool = Assert.Single(chat.Items.OfType<ToolActivityViewModel>());
        Assert.Equal("Drafting data type Pump", tool.Summary);
        Assert.Equal("Drafted Pump.", chat.Items.OfType<ChatMessageViewModel>().Last().Text);

        // Claude Code was started with LogicControl as its only tool server, attached to this window.
        IReadOnlyList<string> args = Assert.Single(claude.Starts);
        Assert.Contains("--strict-mcp-config", args);
        Assert.Contains("/fake/LogicControl.exe", claude.McpConfig, StringComparison.Ordinal);
        Assert.Contains("--attach", claude.McpConfig, StringComparison.Ordinal);

        chat.Shutdown();
    }

    [Fact]
    public async Task AFailedTurnIsShownInTheChat()
    {
        (MainViewModel main, ScriptedClaude claude) = Panel();
        claude.Answer = (_, _) => ["""{"type":"result","subtype":"success","is_error":true,"result":"Claude AI usage limit reached"}"""];
        AssistantViewModel chat = main.Assistant;

        chat.Input = "hi";
        await chat.SendAsync();

        ChatNoticeViewModel notice = Assert.Single(chat.Items.OfType<ChatNoticeViewModel>());
        Assert.True(notice.IsError);
        Assert.Contains("usage limit", notice.Text, StringComparison.Ordinal);
        Assert.False(chat.IsBusy);
        chat.Shutdown();
    }

    [Fact]
    public void TheMcpConfigNamesLogicControlInAttachMode()
    {
        JsonNode config = JsonNode.Parse(AssistantViewModel.McpConfig(@"C:\Program Files\LogicControl\LogicControl.exe", "pipe-1"))!;
        JsonNode server = config["mcpServers"]!["logiccontrol"]!;
        Assert.Equal(@"C:\Program Files\LogicControl\LogicControl.exe", (string?)server["command"]);
        Assert.Equal(new[] { "--mcp", "--attach", "pipe-1" }, ((JsonArray)server["args"]!).Select(a => (string?)a));
    }
}
