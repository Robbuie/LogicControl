namespace LogicControl.Core.Assistant;

/// <summary>
/// Finds the user's Claude Code - the <c>claude</c> command - on this machine.
///
/// <para>In order: a path the user set; the native installer's <c>~/.local/bin</c>; every folder
/// on PATH; npm's global folder; and the copy the VS Code extension carries, newest version first.
/// The file system is passed in so the search is tested without one.</para>
/// </summary>
public sealed class ClaudeCodeLocator(Func<string, bool> fileExists, Func<string, IEnumerable<string>> directories, Func<string, string?> environment)
{
    /// <summary>The real file system and environment.</summary>
    public static ClaudeCodeLocator Default { get; } = new(
        File.Exists,
        d => Directory.Exists(d) ? Directory.EnumerateDirectories(d) : [],
        Environment.GetEnvironmentVariable);

    public string? Find(string? configured = null)
    {
        foreach (string candidate in Candidates(configured))
        {
            if (fileExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Every place looked, in order - also what the panel lists when nothing is found.</summary>
    public IEnumerable<string> Candidates(string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return configured.Trim().Trim('"');
        }

        bool windows = OperatingSystem.IsWindows();
        string[] names = windows ? ["claude.exe", "claude.cmd"] : ["claude"];
        string? home = environment(windows ? "USERPROFILE" : "HOME");

        if (home is not null)
        {
            yield return Path.Combine(home, ".local", "bin", names[0]);
        }

        foreach (string folder in (environment("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (string name in names)
            {
                yield return Path.Combine(folder.Trim('"'), name);
            }
        }

        if (windows && environment("APPDATA") is { } appData)
        {
            yield return Path.Combine(appData, "npm", "claude.cmd");
        }

        if (home is not null)
        {
            // The VS Code extension ships its own binary: anthropic.claude-code-<version>[-platform].
            foreach (string extension in directories(Path.Combine(home, ".vscode", "extensions"))
                .Where(d => Path.GetFileName(d).StartsWith("anthropic.claude-code-", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => VersionOf(Path.GetFileName(d))))
            {
                yield return Path.Combine(extension, "resources", "native-binary", names[0]);
            }
        }
    }

    private static Version VersionOf(string folder)
    {
        string rest = folder["anthropic.claude-code-".Length..];
        int dash = rest.IndexOf('-', StringComparison.Ordinal);
        return Version.TryParse(dash > 0 ? rest[..dash] : rest, out Version? v) ? v : new Version(0, 0);
    }
}
