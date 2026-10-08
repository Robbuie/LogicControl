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
        InputBindings.Add(new KeyBinding(new RelayKey(() => SaveDevelop(saveAs: true)), Key.S, ModifierKeys.Control | ModifierKeys.Shift));
        InputBindings.Add(new KeyBinding(new RelayKey(ReopenLastExport), Key.O, ModifierKeys.Control | ModifierKeys.Shift));

        InputBindings.Add(new KeyBinding(new RelayKey(() =>
        {
            if (ViewModel is { } vm)
            {
                vm.Assistant.IsOpen = !vm.Assistant.IsOpen;
            }
        }), Key.A, ModifierKeys.Control | ModifierKeys.Shift));

        DataContextChanged += OnDataContextChanged;
        Closing += OnClosing;

        Key[] digits = [Key.D1, Key.D2, Key.D3, Key.D4, Key.D5, Key.D6, Key.D7, Key.D8, Key.D9];
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

    /// <summary>Picks another export to compare the open project with, on the Compare tab.</summary>
    private void OnCompareWith(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Compare the open project with another L5X export",
            Filter = L5xFilter,
            CheckFileExists = true,
        };

        if (vm.FilePath is { } current && Path.GetDirectoryName(current) is { } folder && Directory.Exists(folder))
        {
            dialog.InitialDirectory = folder;
        }

        if (dialog.ShowDialog(this) == true)
        {
            vm.SelectedTab = MainViewModel.CompareTab;
            _ = vm.Compare.OpenAsync(dialog.FileName);
        }
    }

    /// <summary>Opens another controller's export beside the main one, on the System tab.</summary>
    private void OnAddController(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Add a controller to the system view",
            Filter = L5xFilter,
            CheckFileExists = true,
            Multiselect = true,
        };

        if (vm.FilePath is { } current && Path.GetDirectoryName(current) is { } folder && Directory.Exists(folder))
        {
            dialog.InitialDirectory = folder;
        }

        if (dialog.ShowDialog(this) == true)
        {
            vm.SelectedTab = MainViewModel.SystemTab;
            _ = AddControllersAsync(vm, dialog.FileNames);
        }
    }

    private static async Task AddControllersAsync(MainViewModel vm, IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            await vm.SystemView.AddAsync(path).ConfigureAwait(true);
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
            // A dropped .lcdev opens on the Develop tab, as it does from Open with.
            if (path.EndsWith(DevelopmentSet.FileExtension, StringComparison.OrdinalIgnoreCase))
            {
                OpenDevelopmentSet(path);
            }
            else
            {
                _ = ViewModel?.OpenAsync(path);
            }
        }
    }

    // ------------------------------------------------------------------ recent files

    /// <summary>
    /// Fills one of File's recent submenus as it opens, so it is never stale: Tag "open" lists
    /// exports and development sets, "compare" and "add" list exports only.
    /// </summary>
    private void OnRecentSubmenuOpened(object sender, RoutedEventArgs e)
    {
        // SubmenuOpened bubbles; only the menu that opened rebuilds.
        if (sender is not MenuItem menu || !ReferenceEquals(e.OriginalSource, menu) || ViewModel is not { } vm)
        {
            return;
        }

        string action = menu.Tag as string ?? "open";
        RecentFiles recent = vm.Recent;
        menu.Items.Clear();

        IEnumerable<RecentFile> exports = recent.Exports;
        if (action != "open" && vm.FilePath is { } current)
        {
            // Comparing or adding the file that is already open means nothing.
            exports = exports.Where(f => !string.Equals(f.Path, Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase));
        }

        int number = 0;
        foreach (RecentFile file in exports)
        {
            menu.Items.Add(RecentItem(file, ++number, action));
        }

        if (action == "open" && recent.DevelopmentSets.Count > 0)
        {
            if (number > 0)
            {
                menu.Items.Add(new Separator());
            }

            menu.Items.Add(new MenuItem { Header = "Development sets", IsEnabled = false });
            foreach (RecentFile file in recent.DevelopmentSets)
            {
                menu.Items.Add(RecentItem(file, ++number, action));
            }
        }

        if (menu.Items.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "(nothing yet)", IsEnabled = false });
            return;
        }

        if (action == "open")
        {
            menu.Items.Add(new Separator());
            var removeMissing = new MenuItem { Header = "_Remove missing files", IsEnabled = recent.HasMissing };
            removeMissing.Click += (_, _) => recent.RemoveMissing();
            menu.Items.Add(removeMissing);

            var clear = new MenuItem { Header = "C_lear the list" };
            clear.Click += (_, _) => recent.Clear();
            menu.Items.Add(clear);
        }
    }

    /// <summary>One recent file: "_1  Line3.L5X" with its folder on the right and the full path as a tip.</summary>
    private MenuItem RecentItem(RecentFile file, int number, string action)
    {
        bool there = ViewModel?.Recent.Exists(file) ?? true;

        // An underscore in a file name is an access key to WPF unless it is doubled.
        string name = file.Name.Replace("_", "__", StringComparison.Ordinal);
        string prefix = number <= 9 ? $"_{number}  " : "    ";

        var item = new MenuItem
        {
            Header = prefix + name + (there ? string.Empty : "  (missing)"),
            InputGestureText = Shorten(file.Folder, 48),
            ToolTip = there ? file.Path : $"{file.Path}\n\nNot there now - a removed drive or an offline share?",
            Opacity = there ? 1.0 : 0.6,
        };

        item.Click += (_, _) => OpenRecent(file, action);
        return item;
    }

    private void OpenRecent(RecentFile file, string action)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        if (!vm.Recent.Exists(file))
        {
            MessageBoxResult remove = MessageBox.Show(
                this,
                $"{file.Path}\n\nis not there now. Take it off the recent list?",
                "LogicControl",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (remove == MessageBoxResult.Yes)
            {
                vm.Recent.Remove(file.Path);
            }

            return;
        }

        switch (action)
        {
            case "compare":
                vm.SelectedTab = MainViewModel.CompareTab;
                _ = vm.Compare.OpenAsync(file.Path);
                break;

            case "add":
                vm.SelectedTab = MainViewModel.SystemTab;
                _ = vm.SystemView.AddAsync(file.Path);
                break;

            default:
                if (file.Kind == RecentKind.DevelopmentSet)
                {
                    OpenDevelopmentSet(file.Path);
                }
                else
                {
                    _ = vm.OpenAsync(file.Path);
                }

                break;
        }
    }

    /// <summary>Ctrl+Shift+O: the newest export on the list that is not the one open now.</summary>
    private void ReopenLastExport()
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        string? current = vm.FilePath is { } open ? Path.GetFullPath(open) : null;
        RecentFile? last = vm.Recent.Exports.FirstOrDefault(
            f => !string.Equals(f.Path, current, StringComparison.OrdinalIgnoreCase) && vm.Recent.Exists(f));
        if (last is not null)
        {
            _ = vm.OpenAsync(last.Path);
        }
    }

    private void OnRevealExport(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.FilePath is { } path)
        {
            Shell.Reveal(this, path);
        }
    }

    private void OnCopyExportPath(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.FilePath is { } path)
        {
            try
            {
                Clipboard.SetText(path);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Another program has the clipboard open; pressing again works.
            }
        }
    }

    /// <summary>"C:\Users\me\...\Line 3\Exports" - the start and the end, which are the parts that say which.</summary>
    private static string Shorten(string folder, int max)
    {
        if (folder.Length <= max)
        {
            return folder;
        }

        string root = Path.GetPathRoot(folder) ?? string.Empty;
        string tail = folder[^(max - root.Length - 4)..];
        int cut = tail.IndexOf(Path.DirectorySeparatorChar, StringComparison.Ordinal);
        return root + "..." + (cut >= 0 ? tail[cut..] : Path.DirectorySeparatorChar + tail);
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

            vm.Assistant.PropertyChanged += OnAssistantChanged;
            vm.LineFocusRequested += OnLineFocusRequested;
            SizeAssistant(vm.Assistant.IsOpen);
        }
    }

    // ------------------------------------------------------------------ assistant

    /// <summary>The width the panel opens at, and the width it had when it was last closed.</summary>
    private GridLength _assistantWidth = new(420);

    private bool _chatFollows = true;

    private void OnAssistantChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModels.Assistant.AssistantViewModel.IsOpen) && sender is ViewModels.Assistant.AssistantViewModel a)
        {
            SizeAssistant(a.IsOpen);
            if (a.IsOpen)
            {
                Dispatcher.InvokeAsync(() => AssistantInput.Focus(), System.Windows.Threading.DispatcherPriority.Input);
            }
        }
    }

    /// <summary>
    /// Opens or closes the panel's column. A star or pixel width on a hidden column would still take
    /// space, so closed is zero; the width the user dragged it to is kept for next time.
    /// </summary>
    private void SizeAssistant(bool open)
    {
        if (!open && AssistantColumn.Width.Value > 0)
        {
            _assistantWidth = AssistantColumn.Width;
        }

        AssistantSplitterColumn.Width = open ? new GridLength(8) : new GridLength(0);
        AssistantColumn.Width = open ? _assistantWidth : new GridLength(0);
        AssistantColumn.MinWidth = open ? 280 : 0;
        FitAssistant();
    }

    /// <summary>Space the tabs keep, however wide the panel is dragged.</summary>
    private const double TabsMinWidth = 360;

    private void OnWorkspaceSizeChanged(object sender, SizeChangedEventArgs e) => FitAssistant();

    /// <summary>
    /// Keeps the Claude panel inside the window. Grid columns with minimum widths that add up to
    /// more than the window are laid out past its right edge - which cut the panel's right side
    /// off on a smaller screen - so the panel gives way first: it shrinks to what is left after the
    /// navigator and the tabs, down to a readable minimum, and the navigator narrows below that.
    /// </summary>
    private void FitAssistant()
    {
        if (AssistantColumn.Width.Value <= 0 || Workspace.ActualWidth <= 0)
        {
            return;
        }

        double room = Workspace.ActualWidth - NavigatorColumn.ActualWidth - 8 - 8 - TabsMinWidth;
        if (room < AssistantColumn.MinWidth)
        {
            NavigatorColumn.Width = new GridLength(Math.Max(NavigatorColumn.MinWidth, NavigatorColumn.ActualWidth - (AssistantColumn.MinWidth - room)));
            room = Workspace.ActualWidth - NavigatorColumn.MinWidth - 8 - 8 - TabsMinWidth;
        }

        double fit = Math.Max(AssistantColumn.MinWidth, room);
        if (AssistantColumn.ActualWidth > fit + 0.5 || AssistantColumn.Width.Value > fit + 0.5)
        {
            AssistantColumn.Width = new GridLength(fit);
        }

        AssistantColumn.MaxWidth = Math.Max(AssistantColumn.MinWidth, Workspace.ActualWidth - NavigatorColumn.MinWidth - 8 - 8 - TabsMinWidth);
    }

    /// <summary>Hands the wheel from a sideways-scrolling table or rung to the chat around it.</summary>
    private void OnChatSideScrollWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        ChatScroll.ScrollToVerticalOffset(ChatScroll.VerticalOffset - e.Delta);
    }

    /// <summary>Copies an answer as the text Claude wrote - tables and rungs included.</summary>
    private void OnCopyChatMessage(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ViewModels.Assistant.ChatMessageViewModel message })
        {
            try
            {
                Clipboard.SetText(message.Text);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // The clipboard is held by another program; trying again is the user's call.
            }
        }
    }

    /// <summary>Enter sends, Shift+Enter is a new line - the chat convention.</summary>
    private void OnAssistantInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0 && ViewModel?.Assistant is { } a)
        {
            e.Handled = true;
            if (a.SendCommand.CanExecute(null))
            {
                _chatFollows = true;
                a.SendCommand.Execute(null);
            }
        }
    }

    /// <summary>Keeps the newest text in view while it streams, unless the user has scrolled up to read.</summary>
    private void OnChatScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0)
        {
            _chatFollows = ChatScroll.VerticalOffset >= ChatScroll.ScrollableHeight - 8;
        }
        else if (_chatFollows)
        {
            ChatScroll.ScrollToEnd();
        }
    }

    private void OnSaveApiKey(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.Assistant is not { } a)
        {
            return;
        }

        string? problem;
        try
        {
            problem = a.SaveKey(ApiKeyBox.Password);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            problem = $"The key could not be stored: {ex.Message}";
        }

        ApiKeyError.Text = problem ?? string.Empty;
        ApiKeyError.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        if (problem is null)
        {
            ApiKeyBox.Clear();
            AssistantInput.Focus();
        }
    }

    private void OnGetApiKey(object sender, RoutedEventArgs e) => Shell.Open(this, "https://console.anthropic.com/settings/keys");

    private void OnInstallClaudeCode(object sender, RoutedEventArgs e) => Shell.Open(this, "https://code.claude.com/docs/en/setup");

    /// <summary>
    /// Opens a console running <c>claude auth login</c>: Claude Code's own sign-in, in a browser,
    /// with the Claude account whose plan the chat should use. The panel checks again afterwards.
    /// </summary>
    private void OnSignInClaudeCode(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.Assistant is not { ClaudeCodeExecutable: { } exe })
        {
            return;
        }

        try
        {
            // cmd's own quoting: /k ""C:\path\claude.exe" auth login" - the outer pair is stripped.
            var info = new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = true,
                Arguments = $"/k \"\"{exe}\" auth login\"",
            };
            using System.Diagnostics.Process? console = System.Diagnostics.Process.Start(info);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Fail("Could not start Claude Code's sign-in", ex);
        }
    }

    private void OnLocateClaudeCode(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Where is claude.exe?",
            Filter = "Claude Code (claude.exe; claude.cmd)|claude.exe;claude.cmd|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) == true)
        {
            ViewModel?.Assistant.UseClaudeCodeAt(dialog.FileName);
        }
    }

    private void OnShowAssistantDrafts(object sender, RoutedEventArgs e) => ViewModel?.Assistant.ShowDrafts();

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
        if (dialog.ShowDialog(this) == true)
        {
            OpenDevelopmentSet(dialog.FileName, confirmed: true);
        }
    }

    /// <summary>Opens a .lcdev on the Develop tab - from the dialog, the recent list or a drop.</summary>
    private void OpenDevelopmentSet(string path, bool confirmed = false)
    {
        if (Develop is not { } d || (!confirmed && !ConfirmDiscard(d)))
        {
            return;
        }

        try
        {
            d.Open(path);
            ViewModel?.StartDeveloping();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Fail("Could not open the development set", ex);
        }
    }

    private void OnDevelopSaveAs(object sender, RoutedEventArgs e) => SaveDevelop(saveAs: true);

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

    /// <summary>Double-clicking a rung on the Logic tab edits it - the draft opens on that rung.</summary>
    private void OnLogicLineMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement { DataContext: LogicLineViewModel line } && ViewModel is { } vm
            && vm.EditRungCommand.CanExecute(line))
        {
            vm.EditRungCommand.Execute(line);
            e.Handled = true;
        }
    }

    private void OnFindingDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid { SelectedItem: FindingRowViewModel row } && ViewModel is { } vm)
        {
            vm.OpenSiteCommand.Execute(row);
        }
    }

    private void OnTagUseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid { SelectedItem: TagUseRowViewModel row } && ViewModel is { } vm)
        {
            vm.OpenSiteCommand.Execute(row);
        }
    }

    /// <summary>
    /// Scrolls the Logic tab to a line a finding or cross-reference opened. The lists are
    /// virtualised, so the row may not exist yet: the panel is asked to bring the index into view,
    /// after the tab switch has laid the list out.
    /// </summary>
    private void OnLineFocusRequested(object? sender, int index)
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            ItemsControl list = ViewModel?.LadderVisible == true ? LadderList : TextList;
            if (FindChild<VirtualizingStackPanel>(list) is { } panel && index < list.Items.Count)
            {
                panel.BringIndexIntoViewPublic(index);
            }
        });
    }

    private static T? FindChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T found)
            {
                return found;
            }

            if (FindChild<T>(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
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
            return;
        }

        // Claude Code and its pipe go with the window.
        ViewModel?.Assistant.Shutdown();
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
