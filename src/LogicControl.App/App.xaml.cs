// UseWPF drops System.IO from the implicit usings; a file path arrives on the command line.
using System.IO;
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

        var viewModel = new MainViewModel();
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
