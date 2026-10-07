// UseWPF drops System.IO from the implicit usings; a file path arrives on the command line.
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using LogicControl.App.Appearance;
using LogicControl.App.Composition;
using LogicControl.App.Diagnostics;
using LogicControl.App.ViewModels;
using LogicControl.App.Views;
using LogicControl.Core.Diagnostics;

namespace LogicControl.App;

/// <summary>
/// Entry point: theme first, then the window, then - if Windows handed us a file, because the
/// app was started from an .L5X's Open with - that file.
///
/// <para>Nothing here may fail quietly. This is a WinExe, so there is no console for a stack
/// trace; every way out of startup ends in a window or in a dialog that names the cause and the
/// log file. Same rule, and mostly the same code, as NetControl.</para>
/// </summary>
public partial class App : Application
{
    private TraceLog? _trace;
    private IDisposable? _systemTheme;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // LogicControl.exe --mcp: no window, LogicControl's tools served to Claude over stdio.
        if (e.Args.Contains("--mcp", StringComparer.OrdinalIgnoreCase))
        {
            RunMcpServer(e.Args);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        _trace = TraceLog.Open(AppPaths.Logs);
        _trace.Info($"{BuildInfo.Describe()} starting.");

        // The previous executable left beside a portable copy by the last update, and finished
        // downloads. Here rather than at the end of an update, because the file being replaced
        // was the one running.
        UpdateApplier.SweepLeftovers(BuildInfo.ExecutablePath, AppPaths.Updates, _trace);

        // Before any window, so nothing flashes the default theme first.
        Theme.Apply(this, AppearanceStore.Load(_trace));
        WindowChromeCommands.Register();

        _systemTheme = SystemTheme.Watch(() =>
        {
            if (Dispatcher.HasShutdownStarted)
            {
                return;
            }

            Dispatcher.InvokeAsync(() =>
            {
                if (Theme.CurrentChoice.Follow == Theme.FollowWindows)
                {
                    Theme.Apply(this, Theme.CurrentChoice);
                }
            });
        });

        // The assistant's key is DPAPI-encrypted for this Windows user; its model and whether the
        // panel was open are remembered between runs.
        AssistantPreferences preferences = AssistantPreferences.Load();
        var viewModel = new MainViewModel(
            new DpapiKeyStore(),
            handler: null,
            model: preferences.Model,
            backend: Enum.TryParse(preferences.Backend, out ViewModels.Assistant.AssistantBackend chosen) ? chosen : null,
            claudeCode: new ViewModels.Assistant.ClaudeCodeEnvironment
            {
                DataFolder = Path.Combine(AppPaths.Data, "claude"),
                SelfExecutable = BuildInfo.ExecutablePath,
                ConfiguredPath = preferences.ClaudeCodePath,
            });
        viewModel.Assistant.ClaudeCodePathChanged += (_, _) =>
        {
            preferences.ClaudeCodePath = viewModel.Assistant.ClaudeCodeConfiguredPath;
            preferences.Save();
        };
        viewModel.Assistant.BackendChanged += (_, _) =>
        {
            preferences.Backend = viewModel.Assistant.Backend.ToString();
            preferences.Save();
        };
        viewModel.Assistant.IsOpen = preferences.Open;
        viewModel.Assistant.ModelChanged += (_, _) =>
        {
            preferences.Model = viewModel.Assistant.Model;
            preferences.Save();
        };
        viewModel.Assistant.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(viewModel.Assistant.IsOpen))
            {
                preferences.Open = viewModel.Assistant.IsOpen;
                preferences.Save();
            }
        };

        var window = new MainWindow { DataContext = viewModel };
        window.Show();

        // Started from a file's Open with: an .lcdev opens on the Develop tab, anything else is read
        // as an export (the reader says plainly if it is not one).
        string? file = e.Args.FirstOrDefault(a => File.Exists(a));
        if (file is not null && file.EndsWith(Core.Authoring.DevelopmentSet.FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                viewModel.Develop.Open(file);
                viewModel.StartDeveloping();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                _trace.Warn($"Could not open {file}.", ex);
            }
        }
        else if (file is not null)
        {
            _ = viewModel.OpenAsync(file);
        }

        _ = CheckForUpdatesAsync(viewModel);
    }

    /// <summary>
    /// Asks whether a newer build has been published - the repository's latest release by default,
    /// a site's own manifest if settings.json names one, and nothing at all if it says
    /// <c>"checkForUpdates": false</c>. Never throws, and nothing waits for it: the window is up
    /// first. Only an available update reaches the status bar; a failed check is logged and stays
    /// quiet, because on a plant network with no route out it fails every time and a warning that
    /// is always there is one nobody reads. Help > Check for updates always answers.
    /// </summary>
    private async Task CheckForUpdatesAsync(MainViewModel viewModel)
    {
        AppSettings settings = AppSettings.Load(AppPaths.SettingsFile, _trace);
        UpdateResult result = await UpdateCheck.RunAsync(settings, BuildInfo.Version, trace: _trace).ConfigureAwait(true);
        viewModel.UpdateStatus = result.IsUpdateAvailable ? result.StatusText : null;
    }

    /// <summary>
    /// Serves LogicControl's tools to Claude in VS Code or the desktop app (see McpServer). Stays
    /// windowless until the client closes stdin, then shuts down. A GUI-subsystem exe can still use
    /// stdio when its parent hands it pipes, which is exactly what an MCP client does.
    /// </summary>
    private void RunMcpServer(string[] args)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // --attach <pipe>: started by the assistant panel's Claude Code. Every tool call goes to the
        // window that started it, so Claude works on the project and drafts on screen.
        int attach = Array.FindIndex(args, a => string.Equals(a, "--attach", StringComparison.OrdinalIgnoreCase));
        if (attach >= 0 && attach + 1 < args.Length)
        {
            var bridge = new Core.Assistant.ToolBridgeClient(args[attach + 1]);
            var attached = new Core.Assistant.McpServer(Core.Assistant.LogicTools.DefinitionsFor(canOpenProjects: false), bridge.Execute, BuildInfo.Version);
            var attachedIn = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            var attachedOut = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            Task.Run(() => attached.RunAsync(attachedIn, attachedOut))
                .ContinueWith(_ => Dispatcher.InvokeAsync(Shutdown), TaskScheduler.Default);
            return;
        }

        int at = Array.FindIndex(args, a => string.Equals(a, "--drafts", StringComparison.OrdinalIgnoreCase));
        string drafts = at >= 0 && at + 1 < args.Length
            ? args[at + 1]
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LogicControl", "Claude drafts" + Core.Authoring.DevelopmentSet.FileExtension);

        var log = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true };
        var host = new Core.Assistant.FileToolHost(drafts, log);

        string? project = args.FirstOrDefault(a => a.EndsWith(".l5x", StringComparison.OrdinalIgnoreCase) && File.Exists(a));
        if (project is not null && host.OpenProject(project) is { } problem)
        {
            log.WriteLine(problem);
        }

        var server = new Core.Assistant.McpServer(host, BuildInfo.Version);
        var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };

        Task.Run(() => server.RunAsync(input, output))
            .ContinueWith(_ => Dispatcher.InvokeAsync(Shutdown), TaskScheduler.Default);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _systemTheme?.Dispose();
        _systemTheme = null;

        _trace?.Info("Stopped.");
        _trace?.Dispose();
        _trace = null;

        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Report("LogicControl hit an error it did not expect.", e.Exception);

        // Handled, so the window survives - an analysis on screen is worth more than a clean exit.
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            Report("LogicControl stopped because of an error on a background thread.", exception);
        }
    }

    private void Report(string headline, Exception exception)
    {
        _trace?.Write(EventSeverity.Error, headline, exception);

        string written = _trace?.FilePath is { } path
            ? Environment.NewLine + Environment.NewLine + $"Written to {path}"
            : string.Empty;

        MessageBox.Show(
            $"{headline}{Environment.NewLine}{Environment.NewLine}{exception.GetType().Name}: {exception.Message}{written}",
            "LogicControl",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
