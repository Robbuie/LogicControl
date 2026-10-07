using System.Diagnostics;
using System.Text;

namespace LogicControl.Core.Assistant;

/// <summary>A running <c>claude</c>: its stdin, stdout and stderr. Faked in tests.</summary>
public interface IClaudeCodeProcess : IDisposable
{
    TextWriter Input { get; }

    TextReader Output { get; }

    /// <summary>Everything written to stderr so far - for the error message when it stops.</summary>
    string ErrorText { get; }

    bool HasExited { get; }

    int? ExitCode { get; }

    void Kill();
}

/// <summary>Starts <c>claude</c> with a set of arguments in a working folder.</summary>
public interface IClaudeCodeLauncher
{
    IClaudeCodeProcess Start(string executable, IReadOnlyList<string> arguments, string workingDirectory);
}

/// <summary>The real thing: a hidden child process with redirected UTF-8 streams.</summary>
public sealed class ClaudeCodeLauncher : IClaudeCodeLauncher
{
    public static ClaudeCodeLauncher Instance { get; } = new();

    public IClaudeCodeProcess Start(string executable, IReadOnlyList<string> arguments, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);

        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = workingDirectory,
        };

        foreach (string a in arguments)
        {
            info.ArgumentList.Add(a);
        }

        Process process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {executable}.");
        return new Running(process);
    }

    private sealed class Running : IClaudeCodeProcess
    {
        private readonly Process _process;
        private readonly StringBuilder _errors = new();

        public Running(Process process)
        {
            _process = process;
            _process.StandardInput.AutoFlush = true;
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    lock (_errors)
                    {
                        _errors.AppendLine(e.Data);
                    }
                }
            };
            _process.BeginErrorReadLine();
        }

        public TextWriter Input => _process.StandardInput;

        public TextReader Output => _process.StandardOutput;

        public string ErrorText
        {
            get
            {
                lock (_errors)
                {
                    return _errors.ToString();
                }
            }
        }

        public bool HasExited => _process.HasExited;

        public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

        public void Kill()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
        }

        public void Dispose()
        {
            Kill();
            _process.Dispose();
        }
    }
}
