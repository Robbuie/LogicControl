// UseWPF drops System.IO from the implicit usings; dropped files are checked on disk.
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using LogicControl.App.Appearance;
using LogicControl.App.Composition;
using LogicControl.App.Diagnostics;
using LogicControl.App.ViewModels;
using LogicControl.App.ViewModels.Develop;
using LogicControl.Core.Authoring;

namespace LogicControl.App.Views;

/// <summary>
/// The main window's code-behind: only what needs a window - file dialogs, drag and drop,
/// shortcuts, and the tree view's selection, which WPF will not bind. Everything else is
/// <see cref="MainViewModel"/>.
/// </summary>
public partial class MainWindow : Window
{
    private const string L5xFilter = "Studio 5000 export (*.L5X)|*.L5X;*.l5x|All files (*.*)|*.*";
    /// <summary>Guards the update check against a second press while one is in flight.</summary>
    private bool _checkingForUpdates;

    private const string SetFilter = "LogicControl development set (*.lcdev)|*.lcdev|All files (*.*)|*.*";

    public MainWindow()
    {
        InitializeComponent();

        Chrome.SetTitleContent(this, Resources["TitleMenu"]);

        Drop += OnDrop;
        DragOver += OnDragOver;

        InputBindings.Add(new KeyBinding(new RelayKey(() => OnOpen(this, new RoutedEventArgs())), Key.O, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayKey(() => ViewModel?.ReloadCommand.Execute(null)), Key.F5, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(new RelayKey(FocusSearch), Key.F, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayKey(ClearSearch), Key.Escape, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(new RelayKey(() =>
        {
            if (ViewModel is { } vm)
            {
                vm.ShowLadder = !vm.ShowLadder;
            }
        }), Key.L, ModifierKeys.Control));

        InputBindings.Add(new KeyBinding(new RelayKey(() => OnDevelopSave(this, new RoutedEventArgs())), Key.S, ModifierKeys.Control));

        DataContextChanged += OnDataContextChanged;
        Closing += OnClosing;

        Key[] digits = [Key.D1, Key.D2, Key.D3, Key.D4, Key.D5, Key.D6, Key.D7];
        for (int i = 0; i < digits.Length; i++)
        {
            int tab = i;
            InputBindings.Add(new KeyBinding(new RelayKey(() => ShowTab(tab)), digits[i], ModifierKeys.Control));
        }
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    // ------------------------------------------------------------------ opening

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open a Studio 5000 L5X export",
            Filter = L5xFilter,
            CheckFileExists = true,
        };

        if (ViewModel?.FilePath is { } current && Path.GetDirectoryName(current) is { } folder && Directory.Exists(folder))
        {
            dialog.InitialDirectory = folder;
        }

        if (dialog.ShowDialog(this) == true)
        {
            _ = ViewModel?.OpenAsync(dialog.FileName);
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = FirstDroppedFile(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (FirstDroppedFile(e) is { } path)
        {
            _ = ViewModel?.OpenAsync(path);
        }
    }

    /// <summary>The first dropped file that exists. Any extension: the reader says plainly if it is not L5X.</summary>
    private static string? FirstDroppedFile(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files.FirstOrDefault(File.Exists)
            : null;

    // ------------------------------------------------------------------ navigation

    private void OnNavigatorSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (ViewModel is { } vm && e.NewValue is NavNodeViewModel node)
        {
            vm.SelectedNode = node;
        }
    }

    private void OnShowTab(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out int index))
        {
            ShowTab(index);
        }
    }

    private void ShowTab(int index)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        if (index == MainViewModel.DevelopTab)
        {
            vm.StartDeveloping();
        }
        else
        {
            vm.SelectedTab = index;
        }
    }

    private void OnShowDevelop(object sender, RoutedEventArgs e) => ViewModel?.StartDeveloping();

    private void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void ClearSearch()
    {
        if (ViewModel is { Filter.Length: > 0 } vm)
        {
            vm.Filter = string.Empty;
        }
    }

    private void OnDismissError(object sender, RoutedEventArgs e) => ViewModel?.DismissError();

    // ------------------------------------------------------------------ develop

    /// <summary>The drafts list groups by kind. Grouping is a view concern, so it is set here.</summary>
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is MainViewModel vm)
        {
            ICollectionView view = CollectionViewSource.GetDefaultView(vm.Develop.Items);
            if (view.GroupDescriptions.Count == 0)
            {
                view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DraftItemViewModel.Group)));
            }
        }
    }

    private DevelopViewModel? Develop => ViewModel?.Develop;

    private void OnDevelopNew(object sender, RoutedEventArgs e)
    {
        if (Develop is { } d && ConfirmDiscard(d))
        {
            d.Load(new DevelopmentSet { ControllerName = d.ControllerName, SoftwareRevision = d.SoftwareRevision }, null);
            ViewModel?.StartDeveloping();
        }
    }

    private void OnDevelopOpen(object sender, RoutedEventArgs e)
    {
        if (Develop is not { } d || !ConfirmDiscard(d))
        {
            return;
        }

        var dialog = new OpenFileDialog { Title = "Open a development set", Filter = SetFilter, CheckFileExists = true };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            d.Open(dialog.FileName);
            ViewModel?.StartDeveloping();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Fail("Could not open the development set", ex);
        }
    }

    private void OnDevelopSave(object sender, RoutedEventArgs e) => SaveDevelop(saveAs: false);

    /// <summary>Saves the set; asks for a path the first time. False when cancelled or failed.</summary>
    private bool SaveDevelop(bool saveAs)
    {
        if (Develop is not { } d)
        {
            return false;
        }

        string? path = saveAs ? null : d.FilePath;
        if (path is null)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Save the development set",
                Filter = SetFilter,
                DefaultExt = DevelopmentSet.FileExtension,
                FileName = Path.GetFileNameWithoutExtension(ViewModel?.FilePath ?? "Logic") + DevelopmentSet.FileExtension,
            };

            if (dialog.ShowDialog(this) != true)
            {
                return false;
            }

            path = dialog.FileName;
        }

        try
        {
            d.Save(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail("Could not save the development set", ex);
            return false;
        }
    }

    private void OnDevelopExport(object sender, RoutedEventArgs e)
    {
        if (Develop is not { CanExport: true } d)
        {
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "Choose a folder for the import files",
            InitialDirectory = Path.GetDirectoryName(d.FilePath ?? ViewModel?.FilePath ?? string.Empty) ?? string.Empty,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            d.ExportImportFiles(dialog.FolderName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Fail("Could not write the import files", ex);
        }
    }

    private void OnDevelopMerge(object sender, RoutedEventArgs e)
    {
        if (Develop is not { CanMerge: true } d || ViewModel?.FilePath is not { } source)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Write the drafts into a copy of the project",
            Filter = L5xFilter,
            DefaultExt = ".L5X",
            InitialDirectory = Path.GetDirectoryName(source) ?? string.Empty,
            FileName = Path.GetFileNameWithoutExtension(source) + "_LogicControl.L5X",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            MergeReport report = d.MergeInto(dialog.FileName);
            MessageBox.Show(
                this,
                $"Wrote {dialog.FileName}.\n\n{report}\n\nOpen it in Studio 5000 with File > Open (type L5X); "
                + "Studio 5000 builds a new .ACD from it and verifies the logic. The original export is unchanged.",
                "Project copy written",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Core.L5x.L5xFormatException or System.Xml.XmlException)
        {
            Fail("Could not write the project copy", ex);
        }
    }

    private void OnIssueDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid { SelectedItem: DraftIssueRowViewModel row })
        {
            Develop?.ShowIssueCommand.Execute(row);
        }
    }

    /// <summary>Unsaved drafts: offer to save before they are replaced or the window closes.</summary>
    private bool ConfirmDiscard(DevelopViewModel d)
    {
        if (!d.IsDirty || d.IsEmpty)
        {
            return true;
        }

        MessageBoxResult answer = MessageBox.Show(
            this,
            "The development set has unsaved changes. Save them first?",
            "LogicControl",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        return answer switch
        {
            MessageBoxResult.Yes => SaveDevelop(saveAs: false),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (Develop is { } d && !ConfirmDiscard(d))
        {
            e.Cancel = true;
        }
    }

    private void Fail(string headline, Exception ex) =>
        MessageBox.Show(this, $"{headline}.\n\n{ex.Message}", "LogicControl", MessageBoxButton.OK, MessageBoxImage.Warning);

    // ------------------------------------------------------------------ menus

    private void OnAppearance(object sender, RoutedEventArgs e) =>
        new AppearanceWindow { Owner = this }.ShowDialog();

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnHowToExport(object sender, RoutedEventArgs e) =>
        MessageBox.Show(
            this,
            "LogicControl reads Studio 5000's L5X export, not the .ACD project file.\n\n"
            + "1. Open the project in Studio 5000 Logix Designer.\n"
            + "2. File > Save As.\n"
            + "3. Set 'Save as type' to 'Logix Designer XML File (*.L5X)'.\n"
            + "4. Save, then open the .L5X here or drop it on this window.\n\n"
            + "A whole-controller export gives the full picture. A routine, program or AOI exported "
            + "on its own also opens, but checks that need the whole controller are skipped.",
            "How to export an L5X",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

    private void OnAbout(object sender, RoutedEventArgs e) =>
        MessageBox.Show(
            this,
            BuildInfo.Describe()
            + "\n\nReads a Rockwell Studio 5000 L5X export and shows its hardware, how it communicates, "
            + "its tags and its logic as ladder, and what looks wrong. Drafts data types, Add-On "
            + "Instructions and routines and writes them as L5X for Studio 5000 to import. It never "
            + "connects to a controller.\n\n"
            + (BuildInfo.ExecutablePath is { } exe ? exe + "\n" : string.Empty)
            + AppPaths.Data + "\n\n" + BuildInfo.ReleasesPage,
            "About LogicControl",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

    // ------------------------------------------------------------------ updates

    /// <summary>
    /// The update check somebody asked for, which - unlike the one at startup - always answers:
    /// current, switched off, could not reach anything, or here is the new one.
    /// </summary>
    private async void OnCheckForUpdates(object sender, RoutedEventArgs e)
    {
        if (_checkingForUpdates)
        {
            return;
        }

        _checkingForUpdates = true;
        try
        {
            AppSettings settings = AppSettings.Load(AppPaths.SettingsFile);
            UpdateResult result = await UpdateCheck.RunAsync(settings, BuildInfo.Version);

            if (ViewModel is { } viewModel)
            {
                viewModel.UpdateStatus = result.IsUpdateAvailable ? result.StatusText : null;
            }

            if (result.IsUpdateAvailable)
            {
                ShowUpdateDialog(result);
                return;
            }

            string message = result.Availability switch
            {
                UpdateAvailability.Current => $"{BuildInfo.Version} is the newest published build.",
                UpdateAvailability.TurnedOff =>
                    "The update check is switched off on this machine, so nothing was contacted.\n\n"
                    + $"It is \"checkForUpdates\": false in {AppPaths.SettingsFile}.\n\n"
                    + $"Published builds are at {BuildInfo.ReleasesPage}",
                _ => $"Could not check: {result.Problem}\n\n"
                    + "That is the usual answer on a network with no route out, and it says nothing "
                    + "about whether a newer build exists.",
            };

            MessageBox.Show(
                this,
                message,
                "Check for updates",
                MessageBoxButton.OK,
                result.Availability == UpdateAvailability.Failed ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        finally
        {
            _checkingForUpdates = false;
        }
    }

    /// <summary>The status-bar line re-runs the check: the release it names may have been replaced since.</summary>
    private void OnUpdateStatusClicked(object sender, MouseButtonEventArgs e)
    {
        OnCheckForUpdates(sender, e);
        e.Handled = true;
    }

    private void OnOpenReleases(object sender, RoutedEventArgs e) => Shell.Open(this, BuildInfo.ReleasesPage);

    private void OnOpenLogFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Logs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Fail($"{AppPaths.Logs} could not be opened", ex);
            return;
        }

        Shell.Open(this, AppPaths.Logs);
    }

    private void ShowUpdateDialog(UpdateResult result)
    {
        var dialog = new UpdateWindow(result, InstallLocation.Describe(BuildInfo.ExecutablePath), BlockedReason())
        {
            Owner = this,
        };

        dialog.ShowDialog();

        if (dialog.StatusAfterClose is { } status && ViewModel is { } viewModel)
        {
            viewModel.UpdateStatus = status;
        }

        if (dialog.ShouldShutdown)
        {
            Application.Current.Shutdown();
        }
    }

    /// <summary>
    /// Why this is not a moment to replace the application, or null. An update closes LogicControl,
    /// and unsaved drafts would go with it - so they are saved first, by the person who wrote them.
    /// </summary>
    private string? BlockedReason() =>
        Develop is { IsDirty: true, IsEmpty: false }
            ? "The development set has unsaved changes. Updating closes LogicControl, and they would be lost. "
                + "Save them first (File > Save development set), then check again."
            : null;

    /// <summary>A command over a delegate, for the window's own key bindings.</summary>
    private sealed class RelayKey(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => action();
    }
}
