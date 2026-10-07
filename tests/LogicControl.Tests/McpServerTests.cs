using System.Text.Json.Nodes;
using LogicControl.Core.Assistant;
using Xunit;

namespace LogicControl.Tests;

public class McpServerTests
{
    private static (McpServer Server, FileToolHost Host, string Drafts) Start()
    {
        string drafts = Path.Combine(Path.GetTempPath(), $"lc-mcp-{Guid.NewGuid():N}.lcdev");
        var host = new FileToolHost(drafts, TextWriter.Null);
        return (new McpServer(host, "0.3.0-test"), host, drafts);
    }

    [Fact]
    public async Task SpeaksJsonRpcOverLines()
    {
        (McpServer server, _, _) = Start();
        string script = string.Join('\n',
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            """{"jsonrpc":"2.0","id":3,"method":"nope"}""",
            "not json");

        var output = new StringWriter();
        await server.RunAsync(new StringReader(script), output);
        List<JsonObject> replies = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => (JsonObject)JsonNode.Parse(l)!).ToList();

        // Four answers: the notification gets none.
        Assert.Equal(4, replies.Count);
        Assert.Equal("logiccontrol", (string?)replies[0]["result"]!["serverInfo"]!["name"]);
        Assert.Contains("open_project", (string?)replies[0]["result"]!["instructions"], StringComparison.Ordinal);
        Assert.Contains(replies[1]["result"]!["tools"]!.AsArray(), t => (string?)t!["name"] == "read_routine");
        Assert.Equal(-32601, (int)replies[2]["error"]!["code"]!);
        Assert.Equal(-32700, (int)replies[3]["error"]!["code"]!);
    }

    [Fact]
    public void OpensAProjectAndSavesDraftsToTheFile()
    {
        (McpServer server, FileToolHost host, string drafts) = Start();
        try
        {
            string path = System.Text.Json.JsonSerializer.Serialize(Fixture.PathOf("Line3.L5X"));
            JsonObject opened = server.Handle("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"open_project\",\"arguments\":{\"path\":" + path + "}}}")!;
            Assert.False((bool)opened["result"]!["isError"]!);
            Assert.NotNull(host.Analysis);

            JsonObject drafted = server.Handle("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"draft_tags","arguments":{"tags":[{"name":"Spare1","data_type":"BOOL"}]}}}""")!;
            Assert.Contains("1 tag(s) drafted", (string?)drafted["result"]!["content"]![0]!["text"], StringComparison.Ordinal);

            Assert.True(File.Exists(drafts));
            Assert.Equal("Spare1", Core.Authoring.DevelopmentSet.Load(drafts).Tags.Single().Name);
        }
        finally
        {
            File.Delete(drafts);
        }
    }
}
