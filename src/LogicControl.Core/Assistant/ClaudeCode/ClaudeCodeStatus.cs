using System.Diagnostics;
using System.Text.Json.Nodes;

namespace LogicControl.Core.Assistant;

public enum ClaudeCodeState
{
    /// <summary>Not checked yet, or the check could not tell - sending is allowed and errors say why.</summary>
    Unknown,

    /// <summary>No <c>claude</c> anywhere <see cref="ClaudeCodeLocator"/> looks.</summary>
    NotInstalled,

    /// <summary>Installed but not signed in.</summary>
    SignedOut,

    /// <summary>Installed and signed in.</summary>
    Ready,
}

/// <summary>What the check found: the state, where claude is, and how it is signed in.</summary>
public sealed record ClaudeCodeStatus(ClaudeCodeState State, string? Executable, string? Detail)
{
    /// <summary>
    /// Finds <c>claude</c> and asks it <c>claude auth status</c> - JSON on stdout, exit code 0 when
    /// signed in and 1 when not. A Claude Code too old to know the command, or one that does not
    /// answer in time, is <see cref="ClaudeCodeState.Unknown"/>: the chat is let through and the
    /// first question's error, if any, says what is wrong.
    /// </summary>
    public static async Task<ClaudeCodeStatus> CheckAsync(ClaudeCodeLocator locator, string? configured, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(locator);
        string? exe = locator.Find(configured);
        if (exe is null)
        {
            return new ClaudeCodeStatus(ClaudeCodeState.NotInstalled, null, null);
        }

        try
        {
            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            info.ArgumentList.Add("auth");
            info.ArgumentList.Add("status");

            using Process process = Process.Start(info) ?? throw new InvalidOperationException("did not start");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return Interpret(exe, process.ExitCode, await output.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            return new ClaudeCodeStatus(ClaudeCodeState.Unknown, exe, "Claude Code did not answer in time.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return new ClaudeCodeStatus(ClaudeCodeState.Unknown, exe, ex.Message);
        }
    }

    /// <summary>Reads <c>claude auth status</c>'s answer.</summary>
    public static ClaudeCodeStatus Interpret(string exe, int exitCode, string output)
    {
        JsonObject? json = null;
        try
        {
            json = JsonNode.Parse(output.Trim()) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
        }

        string? method = (string?)json?["authMethod"];
        if (exitCode == 0 && method is not "none")
        {
            string how = method switch
            {
                "claude.ai" or "oauth_token" => "your Claude plan",
                "api_key" or "api_key_helper" => "an API key (billed per use)",
                null => "signed in",
                _ => method,
            };
            return new ClaudeCodeStatus(ClaudeCodeState.Ready, exe, how);
        }

        if (exitCode == 1 || method == "none")
        {
            return new ClaudeCodeStatus(ClaudeCodeState.SignedOut, exe, null);
        }

        return new ClaudeCodeStatus(ClaudeCodeState.Unknown, exe, output.Trim().Length > 0 ? output.Trim() : null);
    }
}
