using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LogicControl.Core.Assistant;

/// <summary>
/// Carries tool calls from the MCP server Claude Code starts (<c>LogicControl.exe --mcp --attach
/// &lt;pipe&gt;</c>) into the LogicControl window that started Claude Code, over a named pipe that
/// only this Windows user can open.
///
/// <para>Why: the tools have to run against what is on screen - the open project, the Develop
/// tab's drafts - and those live in the window's process, not in the MCP server's. One request per
/// connection, one JSON line each way: <c>{"name", "input"}</c> in, <c>{"text", "isError"}</c>
/// out.</para>
/// </summary>
public sealed class ToolBridgeServer : IDisposable
{
    private readonly Func<string, JsonElement, Task<ToolResult>> _execute;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    /// <param name="execute">Runs a tool. In the app it posts the call to the UI thread.</param>
    public ToolBridgeServer(string pipeName, Func<string, JsonElement, Task<ToolResult>> execute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        PipeName = pipeName;
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _loop = Task.Run(AcceptAsync);
    }

    public string PipeName { get; }

    /// <summary>A fresh, unguessable pipe name for this process.</summary>
    public static string NewPipeName() => $"LogicControl-{Environment.ProcessId}-{Guid.NewGuid():N}";

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return;
            }

            _ = ServeAsync(pipe);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        await using (pipe.ConfigureAwait(false))
        {
            try
            {
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

                string? line = await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }

                ToolResult result;
                try
                {
                    JsonObject request = JsonNode.Parse(line) as JsonObject ?? throw new JsonException("not an object");
                    string name = (string?)request["name"] ?? throw new JsonException("no tool name");
                    JsonElement input = JsonSerializer.SerializeToElement(request["input"] ?? new JsonObject());
                    result = await _execute(name, input).ConfigureAwait(false);
                }
                catch (JsonException ex)
                {
                    result = ToolResult.Fail($"Bad request from the MCP server: {ex.Message}");
                }

                await writer.WriteLineAsync(new JsonObject { ["text"] = result.Text, ["isError"] = result.IsError }.ToJsonString()).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The client went away mid-call; nothing to answer.
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _stop.Dispose();
    }
}

/// <summary>The MCP server's side of <see cref="ToolBridgeServer"/>: each tool call goes to the window.</summary>
public sealed class ToolBridgeClient(string pipeName, TimeSpan? timeout = null)
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMinutes(2);

    public ToolResult Execute(string name, JsonElement input) => ExecuteAsync(name, input).GetAwaiter().GetResult();

    public async Task<ToolResult> ExecuteAsync(string name, JsonElement input)
    {
        using var timeout = new CancellationTokenSource(_timeout);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(TimeSpan.FromSeconds(10), timeout.Token).ConfigureAwait(false);

            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
            await writer.WriteLineAsync(new JsonObject { ["name"] = name, ["input"] = JsonNode.Parse(input.GetRawText()) }.ToJsonString()).ConfigureAwait(false);

            string? line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (line is null || JsonNode.Parse(line) is not JsonObject reply)
            {
                return ToolResult.Fail("LogicControl closed the connection without answering.");
            }

            return new ToolResult((string?)reply["text"] ?? string.Empty, (bool?)reply["isError"] ?? false, ChangedDrafts: false);
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Fail("LogicControl did not answer in time. Is the window busy, or closed?");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return ToolResult.Fail($"Could not reach the LogicControl window ({ex.Message}). Is it still open?");
        }
    }
}
