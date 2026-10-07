using System.Text.Json;
using System.Text.Json.Nodes;
using LogicControl.Core.Analysis;
using LogicControl.Core.Authoring;

namespace LogicControl.Core.Assistant;

/// <summary>
/// LogicControl's tools as a Model Context Protocol server over stdio, so Claude in VS Code (the
/// Claude Code extension) or the Claude desktop app can use them with the user's own Claude plan -
/// no API key, no per-use billing.
///
/// <para>Started as <c>LogicControl.exe --mcp [export.L5X] [--drafts file.lcdev]</c>. One JSON-RPC
/// message per line on stdin, one per line on stdout; anything for a human goes to stderr, because
/// a stray line on stdout is a protocol error the client cannot recover from. Only the parts of the
/// protocol a tool server needs: initialize, tools/list, tools/call, ping.</para>
///
/// <para>Drafts are kept in a .lcdev file, saved after every change, which LogicControl opens with
/// File > Open development set - so what Claude drafts in VS Code is reviewed, checked and
/// exported in the app exactly like anything else.</para>
/// </summary>
public sealed class McpServer
{
    public const string ProtocolVersion = "2025-06-18";

    private readonly LogicTools _tools;
    private readonly IToolHost _host;
    private readonly string _version;

    public McpServer(IToolHost host, string version)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _tools = new LogicTools(host);
        _version = version;
    }

    /// <summary>Serves until stdin closes.</summary>
    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }

            if (line.Trim().Length == 0)
            {
                continue;
            }

            JsonObject? reply = Handle(line);
            if (reply is not null)
            {
                await output.WriteLineAsync(reply.ToJsonString()).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Answers one message, or returns null for a notification.</summary>
    public JsonObject? Handle(string line)
    {
        JsonObject request;
        try
        {
            request = JsonNode.Parse(line) as JsonObject ?? throw new JsonException("not an object");
        }
        catch (JsonException ex)
        {
            return Error(null, -32700, $"Parse error: {ex.Message}");
        }

        JsonNode? id = request["id"]?.DeepClone();
        string method = (string?)request["method"] ?? string.Empty;
        bool isNotification = request["id"] is null;

        if (isNotification)
        {
            return null;
        }

        try
        {
            JsonNode result = method switch
            {
                "initialize" => Initialize(request["params"] as JsonObject),
                "ping" => new JsonObject(),
                "tools/list" => ListTools(),
                "tools/call" => CallTool(request["params"] as JsonObject),
                _ => throw new MissingMethodException(method),
            };

            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }
        catch (MissingMethodException)
        {
            return Error(id, -32601, $"Method not found: {method}");
        }
        catch (ArgumentException ex)
        {
            return Error(id, -32602, ex.Message);
        }
    }

    private JsonObject Initialize(JsonObject? parameters)
    {
        string version = (string?)parameters?["protocolVersion"] ?? ProtocolVersion;
        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = "logiccontrol", ["version"] = _version },
            ["instructions"] = AssistantPrompt.System + (_host is FileToolHost f
                ? $"\n\nIn this session drafts are saved to {f.DraftsPath}; tell the user to open that file in LogicControl (File > Open development set) to review and export them."
                + (f.Analysis is null ? " No project is open yet: ask for the path of an L5X export and call open_project." : $" The open project is {f.Analysis.Project.SourcePath}.")
                : string.Empty),
        };
    }

    private JsonObject ListTools()
    {
        var tools = new JsonArray();
        foreach (ToolDefinition d in _tools.Definitions)
        {
            tools.Add(new JsonObject
            {
                ["name"] = d.Name,
                ["description"] = d.Description,
                ["inputSchema"] = d.InputSchema.DeepClone(),
                ["annotations"] = new JsonObject { ["readOnlyHint"] = !d.Writes, ["destructiveHint"] = false },
            });
        }

        return new JsonObject { ["tools"] = tools };
    }

    private JsonObject CallTool(JsonObject? parameters)
    {
        string name = (string?)parameters?["name"] ?? throw new ArgumentException("tools/call needs a name.");
        JsonElement arguments = JsonSerializer.SerializeToElement(parameters?["arguments"] ?? new JsonObject());
        ToolResult result = _tools.Execute(name, arguments);

        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = result.Text }),
            ["isError"] = result.IsError,
        };
    }

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };
}

/// <summary>
/// The MCP server's view of the world: a project opened by path, and drafts in a .lcdev file
/// that is saved whenever a tool changes them.
/// </summary>
public sealed class FileToolHost : IToolHost
{
    private readonly TextWriter _log;

    public FileToolHost(string draftsPath, TextWriter log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(draftsPath);
        DraftsPath = draftsPath;
        _log = log;
        Drafts = File.Exists(draftsPath) ? DevelopmentSet.Load(draftsPath) : new DevelopmentSet();

        // A baseline, so the first change Claude makes is recorded as that change alone.
        new Authoring.History.RevisionHistory(Drafts).Record(Drafts, $"Opened {Path.GetFileName(draftsPath)}", Authoring.History.RevisionAuthor.File);
    }

    public string DraftsPath { get; }

    public ProjectAnalysis? Analysis { get; private set; }

    public DevelopmentSet Drafts { get; }

    public bool CanOpenProjects => true;

    public string? OpenProject(string path)
    {
        try
        {
            Analysis = ProjectAnalysis.Open(path);
            if (Drafts.IsEmpty)
            {
                Drafts.ControllerName = Analysis.Project.Controller.Name;
                Drafts.SoftwareRevision = Analysis.Project.SoftwareRevision ?? Drafts.SoftwareRevision;
            }

            _log.WriteLine($"Opened {path}.");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or L5x.L5xFormatException)
        {
            return $"Could not open {path}: {ex.Message}";
        }
    }

    public void DraftsChanged(string summary)
    {
        // The history goes in the file with the drafts, so a session in VS Code leaves the same
        // reviewable, revertable trail as one in the app.
        new Authoring.History.RevisionHistory(Drafts).Record(Drafts, summary, Authoring.History.RevisionAuthor.Claude);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(DraftsPath))!);
            Drafts.Save(DraftsPath);
            _log.WriteLine($"Drafts saved to {DraftsPath}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.WriteLine($"Could not save drafts to {DraftsPath}: {ex.Message}");
        }
    }
}
