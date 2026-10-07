using System.IO;
using LogicControl.Core.Assistant;

namespace LogicControl.App.ViewModels.Assistant;

/// <summary>Which back end the assistant panel uses.</summary>
public enum AssistantBackend
{
    /// <summary>The user's own Claude Code, signed in with their Claude plan. No API key, no credits.</summary>
    ClaudeCode,

    /// <summary>The Messages API with an API key, billed per use.</summary>
    ApiKey,
}

/// <summary>
/// Everything the panel needs to run Claude Code, gathered so a test can swap it: where to look
/// for <c>claude</c>, how to start it, how to check it is signed in, where to write its two small
/// files, and the LogicControl.exe Claude Code should start as its tool server.
/// </summary>
public sealed class ClaudeCodeEnvironment
{
    public ClaudeCodeLocator Locator { get; init; } = ClaudeCodeLocator.Default;

    public IClaudeCodeLauncher Launcher { get; init; } = ClaudeCodeLauncher.Instance;

    /// <summary>Checks for <c>claude</c> and its sign-in. The argument is a path the user set, if any.</summary>
    public Func<string?, Task<ClaudeCodeStatus>>? Check { get; init; }

    /// <summary>Where the MCP config and the system prompt are written, and where claude runs.</summary>
    public string DataFolder { get; init; } = Path.Combine(Path.GetTempPath(), "LogicControl", "claude");

    /// <summary>
    /// LogicControl.exe, which Claude Code starts with <c>--mcp --attach &lt;pipe&gt;</c>. Null in a
    /// test that never starts a real Claude Code.
    /// </summary>
    public string? SelfExecutable { get; init; }

    /// <summary>A path to claude the user chose, overriding the search.</summary>
    public string? ConfiguredPath { get; set; }

    internal Task<ClaudeCodeStatus> CheckAsync() =>
        Check?.Invoke(ConfiguredPath) ?? ClaudeCodeStatus.CheckAsync(Locator, ConfiguredPath);
}
