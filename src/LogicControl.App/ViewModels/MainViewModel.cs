using System.IO;
using System.Net.Http;
using System.Globalization;
using LogicControl.App.Composition;
using LogicControl.App.ViewModels.Assistant;
using LogicControl.App.ViewModels.Develop;
using LogicControl.Core.Analysis;
using LogicControl.Core.L5x;
using LogicControl.Core.Model;

namespace LogicControl.App.ViewModels;

/// <summary>
/// The main window's state: the opened export, every grid's rows, the navigator, the filter.
///
/// <para>Free of WPF types on purpose, the way NetControl's view models are: everything here can
/// be built and checked by a test with no window and no message pump. Opening a file is the one
/// thing that touches a disk, and it runs on the thread pool so a 40 MB export never freezes the
/// window - the await brings the result back on whatever context called it, which in the app is
/// the UI thread.</para>
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    public const int OverviewTab = 0;
    public const int HardwareTab = 1;
    public const int CommsTab = 2;
    public const int SystemTab = 3;
    public const int TagsTab = 4;
    public const int LogicTab = 5;
    public const int FindingsTab = 6;
    public const int DevelopTab = 7;

    private ProjectAnalysis? _analysis;
    private string? _filePath;
    private string? _opening;
    private bool _isBusy;
    private string? _error;
    private string _filter = string.Empty;
    private int _selectedTab;
    private OverviewViewModel? _overview;
    private RoutineViewModel? _routine;
    private TagRowViewModel? _selectedTag;
    private HardwareRowViewModel? _selectedModule;
    private NavNodeViewModel? _selectedNode;
    private bool _showLadder = true;
    private bool _developing;
    private bool _showOriginal;
    private string? _updateStatus;

    private IReadOnlyList<HardwareRowViewModel> _allHardware = [];
    private IReadOnlyList<CommRowViewModel> _allComms = [];
    private IReadOnlyList<TagRowViewModel> _allTags = [];
    private IReadOnlyList<FindingRowViewModel> _allFindings = [];

    /// <param name="keys">Where the assistant's API key is kept: DPAPI in the app, memory by default.</param>
    /// <param name="handler">An HTTP handler for the assistant - a scripted one in tests.</param>
    /// <param name="model">The assistant's model, as last chosen.</param>
    public MainViewModel(IApiKeyStore? keys = null, Func<HttpMessageHandler?>? handler = null, string? model = null)
    {
        SystemView = new SystemViewModel(this);
        Assistant = new AssistantViewModel(this, keys ?? new MemoryKeyStore(), handler, model);
        Assistant.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AssistantViewModel.IsOpen))
            {
                OnPropertyChanged(nameof(ShowWorkspace));
                OnPropertyChanged(nameof(IsEmpty));
            }
        };

        ReloadCommand = new RelayCommand(() => _ = ReloadAsync(), () => _filePath is not null && !_isBusy);
        CloseCommand = new RelayCommand(Close, () => _analysis is not null);
        ClearFilterCommand = new RelayCommand(() => Filter = string.Empty);
        ShowOperandCommand = new RelayParameterCommand(o => ShowOperand(o as string), o => o is string { Length: > 0 });
        OpenSiteCommand = new RelayParameterCommand(o => OpenSite(o), o => SiteOf(o) is not null);
        StartDevelopingCommand = new RelayCommand(StartDeveloping);
        EditRoutineCommand = new RelayCommand(() => EditRung(null), CanEditRoutine);
        EditModuleCommand = new RelayParameterCommand(
            o =>
            {
                if ((o as HardwareRowViewModel ?? _selectedModule) is { CanDraft: true } row && Develop.EditCopyOf(row.Node.Module))
                {
                    StartDeveloping();
                }
            },
            o => (o as HardwareRowViewModel ?? _selectedModule) is { CanDraft: true });
        EditRungCommand = new RelayParameterCommand(o => EditRung(o as LogicLineViewModel), _ => CanEditRoutine());
        DiscardEditsCommand = new RelayCommand(
            () =>
            {
                if (_routine?.Routine is { } r)
                {
                    Develop.DiscardEdits(r);
                }
            },
            () => _routine?.IsEdited == true);
        Develop.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DevelopViewModel.Title))
            {
                OnPropertyChanged(nameof(DevelopHeader));
            }
        };

        // The Logic tab shows an edited routine as the draft has it, so it follows every change.
        Develop.Edited += (_, _) => RefreshRoutine();
        Develop.Revised += (_, _) => RefreshRoutine();
    }

    // ------------------------------------------------------------------ state

    public ProjectAnalysis? Analysis => _analysis;

    public bool HasProject => _analysis is not null;

    public bool IsEmpty => _analysis is null && !_isBusy && !_developing && !Assistant.IsOpen;

    /// <summary>The tabs are on screen: a project is open, or drafts are being written without one.</summary>
    public bool ShowWorkspace => _analysis is not null || _developing || Assistant.IsOpen;

    /// <summary>The Claude panel on the right of the window.</summary>
    public AssistantViewModel Assistant { get; }

    /// <summary>The System tab: the controller drawn as a system, and the plant when more are added.</summary>
    public SystemViewModel SystemView { get; }

    /// <summary>The Develop tab: drafts, their editors, checks and exports.</summary>
    public DevelopViewModel Develop { get; } = new();

    /// <summary>The status bar's update line - only set when a newer build is published. Click it to update.</summary>
    public string? UpdateStatus
    {
        get => _updateStatus;
        set => SetProperty(ref _updateStatus, value);
    }

    public string DevelopHeader => Develop.IsDirty ? "Develop *" : "Develop";

    /// <summary>Opens the Develop tab - with or without a project open.</summary>
    public RelayCommand StartDevelopingCommand { get; }

    /// <summary>
    /// Edits the routine on the Logic tab in place: a draft of the same name replaces it on export
    /// or merge, and the Logic tab shows the draft with every changed rung marked.
    /// </summary>
    public RelayCommand EditRoutineCommand { get; }

    /// <summary>Drafts a change to a Generic Ethernet module - its RPI, sizes, address - in the Develop tab.</summary>
    public RelayParameterCommand EditModuleCommand { get; }

    /// <summary>Edits one rung - what double-clicking a rung on the Logic tab does.</summary>
    public RelayParameterCommand EditRungCommand { get; }

    /// <summary>Drops the draft that edits the routine on screen. The history keeps it.</summary>
    public RelayCommand DiscardEditsCommand { get; }

    /// <summary>The Logic tab shows the project's own routine even while it is being edited.</summary>
    public bool ShowOriginal
    {
        get => _showOriginal;
        set
        {
            if (SetProperty(ref _showOriginal, value))
            {
                RefreshRoutine(force: true);
            }
        }
    }

    /// <summary>The routine on screen has a draft editing it (whether or not the original is being shown).</summary>
    public bool RoutineHasEdits => _routine is not null && Develop.EditOf(_routine.Routine) is not null;

    private bool CanEditRoutine() =>
        _routine is { Routine.IsProtected: false } rv
        && (rv.Routine.Language == RoutineLanguage.Ladder || rv.Routine.OwnerIsAoi);

    private void EditRung(LogicLineViewModel? line)
    {
        if (_routine?.Routine is not { } r)
        {
            return;
        }

        int? at = line is { IsRemoved: false } ? line.Location : null;
        Develop.Edit(r, at);
        StartDeveloping();
    }

    /// <summary>The routine as the Logic tab should draw it: the project's, or the draft that edits it.</summary>
    private RoutineViewModel ViewOf(RoutineInfo routine) =>
        new(routine, _analysis?.Project.AddOnInstructions, _showOriginal ? null : Develop.EditOf(routine));

    private void RefreshRoutine(bool force = false)
    {
        if (_routine is not { } shown)
        {
            return;
        }

        bool edited = !_showOriginal && Develop.EditOf(shown.Routine) is not null;
        if (force || edited || shown.IsEdited)
        {
            Routine = ViewOf(shown.Routine);
        }

        OnPropertyChanged(nameof(RoutineHasEdits));
    }

    public void StartDeveloping()
    {
        _developing = true;
        SelectedTab = DevelopTab;
        OnPropertyChanged(nameof(ShowWorkspace));
        OnPropertyChanged(nameof(IsEmpty));
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsEmpty));
                ReloadCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>Why the last open failed, in words for the person who picked the file. Null when it did not.</summary>
    public string? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => _error is not null;

    public string? FilePath => _filePath;

    public string Title => _filePath is null ? "LogicControl" : $"LogicControl - {Path.GetFileName(_filePath)}";

    public string StatusText
    {
        get
        {
            if (_isBusy)
            {
                return $"Reading {Path.GetFileName(_opening)}...";
            }

            if (_analysis is not { } a)
            {
                return "Open a Studio 5000 L5X export to begin.";
            }

            return string.Join("   ·   ", new[]
            {
                a.Project.SourcePath,
                $"{a.Project.Modules.Count} modules",
                $"{a.TagCount.ToString("N0", CultureInfo.InvariantCulture)} tags",
                $"{a.RungCount.ToString("N0", CultureInfo.InvariantCulture)} rungs",
                $"read in {a.Elapsed.TotalMilliseconds.ToString("N0", CultureInfo.InvariantCulture)} ms",
            });
        }
    }

    public int SelectedTab
    {
        get => _selectedTab;
        set => SetProperty(ref _selectedTab, value);
    }

    /// <summary>Narrows every grid to rows containing this text, ignoring case.</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value ?? string.Empty))
            {
                ApplyFilter();
            }
        }
    }

    public OverviewViewModel? Overview
    {
        get => _overview;
        private set => SetProperty(ref _overview, value);
    }

    public IReadOnlyList<NavNodeViewModel> Navigator { get; private set; } = [];

    public IReadOnlyList<HardwareRowViewModel> Hardware { get; private set; } = [];

    public IReadOnlyList<CommRowViewModel> Communications { get; private set; } = [];

    public IReadOnlyList<TagRowViewModel> Tags { get; private set; } = [];

    public IReadOnlyList<FindingRowViewModel> Findings { get; private set; } = [];

    /// <summary>Tab headers carry their counts, so a filter that empties a tab is visible from the others.</summary>
    public string HardwareHeader => $"Hardware  {Hardware.Count}";

    public string CommsHeader => $"Communications  {Communications.Count}";

    public string TagsHeader => $"Tags  {Tags.Count}";

    public string FindingsHeader => $"Findings  {Findings.Count}";

    public RoutineViewModel? Routine
    {
        get => _routine;
        private set
        {
            if (SetProperty(ref _routine, value))
            {
                OnPropertyChanged(nameof(LadderVisible));
                OnPropertyChanged(nameof(TextVisible));
                OnPropertyChanged(nameof(RoutineHasEdits));
                EditRoutineCommand.NotifyCanExecuteChanged();
                EditRungCommand.NotifyCanExecuteChanged();
                DiscardEditsCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public TagRowViewModel? SelectedTag
    {
        get => _selectedTag;
        set => SetProperty(ref _selectedTag, value);
    }

    public HardwareRowViewModel? SelectedModule
    {
        get => _selectedModule;
        set
        {
            if (SetProperty(ref _selectedModule, value))
            {
                EditModuleCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>The navigator's selection. Setting it opens what it points at.</summary>
    public NavNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (SetProperty(ref _selectedNode, value) && value is not null)
            {
                Navigate(value);
            }
        }
    }

    /// <summary>
    /// Ladder drawn with rails, contacts, coils and boxes (true), or the neutral text Studio 5000
    /// stores (false). Ladder by default; the text is still a click away for copying a rung.
    /// </summary>
    public bool ShowLadder
    {
        get => _showLadder;
        set
        {
            if (SetProperty(ref _showLadder, value))
            {
                OnPropertyChanged(nameof(ShowText));
                OnPropertyChanged(nameof(LadderVisible));
                OnPropertyChanged(nameof(TextVisible));
            }
        }
    }

    /// <summary>The drawn view is on screen: ladder chosen and the open routine is ladder.</summary>
    public bool LadderVisible => _showLadder && _routine?.IsLadder == true;

    /// <summary>The text view is on screen: chosen, or the routine is ST and cannot be drawn.</summary>
    public bool TextVisible => !LadderVisible;

    public bool ShowText
    {
        get => !_showLadder;
        set => ShowLadder = !value;
    }

    /// <summary>
    /// Opens a routine at a rung: a <see cref="FindingSite"/>, a finding row (its first site) or a
    /// cross-reference row. What the findings list's links and a double-click on a use run.
    /// </summary>
    public RelayParameterCommand OpenSiteCommand { get; }

    /// <summary>
    /// Raised after <see cref="OpenSite"/> has put a routine on the Logic tab: the index in
    /// <see cref="RoutineViewModel.Lines"/> to scroll into view. The window does the scrolling.
    /// </summary>
    public event EventHandler<int>? LineFocusRequested;

    /// <summary>Opens the tag an operand names - what clicking an operand in the ladder does.</summary>
    public RelayParameterCommand ShowOperandCommand { get; }

    public RelayCommand ReloadCommand { get; }

    public RelayCommand CloseCommand { get; }

    public RelayCommand ClearFilterCommand { get; }

    // ------------------------------------------------------------------ actions

    /// <summary>Reads and analyses <paramref name="path"/> off the UI thread. Never throws.</summary>
    public async Task OpenAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // The file being opened is not the file on screen until it has been read: a failed open
        // leaves the previous project, its title and its path exactly as they were.
        _opening = path;
        Error = null;
        IsBusy = true;
        RaiseAll();

        try
        {
            ProjectAnalysis analysis = await Task.Run(() => ProjectAnalysis.Open(path)).ConfigureAwait(true);
            _filePath = path;
            Load(analysis);
        }
        catch (L5xFormatException ex)
        {
            Error = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = $"Could not read {path}: {ex.Message}";
        }
        finally
        {
            _opening = null;
            IsBusy = false;
            RaiseAll();
        }
    }

    public Task ReloadAsync() => _filePath is null ? Task.CompletedTask : OpenAsync(_filePath);

    /// <summary>Shows an analysis that has already been built - what <see cref="OpenAsync"/> ends with.</summary>
    public void Load(ProjectAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        _analysis = analysis;
        _filePath ??= analysis.Project.SourcePath;
        Develop.AttachProject(analysis.Project);

        // Findings by subject, so a module's row and a routine's node can show their worst one.
        ILookup<string, Finding> bySubject = analysis.Findings.ToLookup(f => f.Subject, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ModuleInfo> modules = analysis.Project.Modules
            .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        _allHardware = analysis.Hardware
            .SelectMany(r => r.SelfAndDescendants())
            .Select(n => new HardwareRowViewModel(
                n,
                FindingsFor(n.Module, bySubject),
                analysis.CrossReference.ModuleTagUses.TryGetValue(ModuleTags.PrefixOf(n.Module, modules), out int uses) ? uses : 0))
            .ToList();

        _allComms = analysis.Communications.Select(c => new CommRowViewModel(c)).ToList();

        _allTags = analysis.Project.AllTags
            .Select(t => new TagRowViewModel(t, analysis.CrossReference.UsesOf(t)))
            .OrderBy(t => t.Tag.Scope is null ? 0 : 1)
            .ThenBy(t => t.Scope, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _allFindings = analysis.Findings.Select(f => new FindingRowViewModel(f)).ToList();

        Overview = new OverviewViewModel(analysis);
        Navigator = NavigatorBuilder.Build(analysis, bySubject);
        Routine = null;
        SelectedTag = null;
        SelectedModule = null;

        // Open the main routine of the first scheduled program, so the logic tab is never blank.
        RoutineInfo? first = FirstRoutine(analysis.Project);
        if (first is not null)
        {
            Routine = ViewOf(first);
        }

        ApplyFilter();
        SystemView.Rebuild();
        RaiseAll();
    }

    /// <summary>Shows a module on the Hardware tab - what clicking it in the system view does.</summary>
    public void ShowModule(HardwareNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        Filter = string.Empty;
        SelectedModule = _allHardware.FirstOrDefault(r => ReferenceEquals(r.Node, node));
        SelectedTab = HardwareTab;
    }

    /// <summary>Hides the error banner. The project on screen, if any, stays.</summary>
    public void DismissError() => Error = null;

    public void Close()
    {
        Develop.AttachProject(null);
        _analysis = null;
        _filePath = null;
        _allHardware = [];
        _allComms = [];
        _allTags = [];
        _allFindings = [];
        Overview = null;
        Navigator = [];
        Routine = null;
        SelectedTag = null;
        SelectedModule = null;
        Error = null;
        ApplyFilter();
        SystemView.Rebuild();
        RaiseAll();
    }

    /// <summary>Opens what a navigator node points at, on the tab that shows it.</summary>
    public void Navigate(NavNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);

        switch (node.Target)
        {
            case RoutineInfo routine:
                Routine = ViewOf(routine);
                SelectedTab = LogicTab;
                break;

            case HardwareNode hardware:
                ShowModule(hardware);
                break;

            case AoiInfo aoi when aoi.Routines.Count > 0:
                Routine = ViewOf(aoi.Routines[0]);
                SelectedTab = LogicTab;
                break;

            case string tab when int.TryParse(tab, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index):
                SelectedTab = index;
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Opens the routine a site names on the Logic tab, marks the rung and asks the window to
    /// scroll to it. Returns false when the routine is not in the open project.
    /// </summary>
    public bool OpenSite(object? target)
    {
        if (SiteOf(target) is not { } site || _analysis is null)
        {
            return false;
        }

        RoutineInfo? routine = _analysis.Project.AllRoutines
            .FirstOrDefault(r => string.Equals(r.QualifiedName, site.Routine, StringComparison.OrdinalIgnoreCase));
        if (routine is null)
        {
            return false;
        }

        if (_routine is null || !ReferenceEquals(_routine.Routine, routine))
        {
            Routine = ViewOf(routine);
        }

        // Structured text cannot be drawn, and a highlighted ST line needs the text view; a rung
        // keeps whichever view the person chose.
        int index = _routine!.Highlight(site.Location);
        SelectedTab = LogicTab;
        if (index >= 0)
        {
            LineFocusRequested?.Invoke(this, index);
        }

        return true;
    }

    private static FindingSite? SiteOf(object? target) => target switch
    {
        FindingSite site => site,
        FindingRowViewModel row => row.Sites.FirstOrDefault(),
        TagUseRowViewModel use when use.Use.Routine != "(alias)" => FindingSite.Of(use.Use),
        Core.Logic.TagUse use when use.Routine != "(alias)" => FindingSite.Of(use),
        _ => null,
    };

    /// <summary>
    /// Shows the tag an operand refers to on the Tags tab: <c>Conveyor.Speed[2]</c> opens
    /// <c>Conveyor</c>. A program tag in the routine's own program wins over a controller tag of the
    /// same name, which is how Logix itself resolves it. A literal or an expression with no tag in
    /// it does nothing.
    /// </summary>
    public void ShowOperand(string? operand)
    {
        if (string.IsNullOrWhiteSpace(operand) || _analysis is null)
        {
            return;
        }

        string? name = Core.Logic.TagReference.BaseNames(operand).FirstOrDefault();
        if (name is null)
        {
            return;
        }

        string? owner = _routine?.Routine.Owner;
        TagRowViewModel? row =
            _allTags.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(t.Tag.Scope, owner, StringComparison.OrdinalIgnoreCase))
            ?? _allTags.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase) && t.Tag.Scope is null)
            ?? _allTags.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

        Filter = name;
        SelectedTag = row;
        SelectedTab = TagsTab;
    }

    // ------------------------------------------------------------------ internals

    private void ApplyFilter()
    {
        string f = _filter.Trim();
        bool all = f.Length == 0;
        bool Match(string text) => all || text.Contains(f, StringComparison.OrdinalIgnoreCase);

        Hardware = _allHardware.Where(r => Match(r.SearchText)).ToList();
        Communications = _allComms.Where(r => Match(r.SearchText)).ToList();
        Tags = _allTags.Where(r => Match(r.SearchText)).ToList();
        Findings = _allFindings.Where(r => Match(r.SearchText)).ToList();

        OnPropertyChanged(nameof(Hardware));
        OnPropertyChanged(nameof(Communications));
        OnPropertyChanged(nameof(Tags));
        OnPropertyChanged(nameof(Findings));
        OnPropertyChanged(nameof(HardwareHeader));
        OnPropertyChanged(nameof(CommsHeader));
        OnPropertyChanged(nameof(TagsHeader));
        OnPropertyChanged(nameof(FindingsHeader));
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(Analysis));
        OnPropertyChanged(nameof(HasProject));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ShowWorkspace));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(Navigator));
        ReloadCommand.NotifyCanExecuteChanged();
        CloseCommand.NotifyCanExecuteChanged();
    }

    private static IEnumerable<Finding> FindingsFor(ModuleInfo module, ILookup<string, Finding> bySubject)
    {
        IEnumerable<Finding> byName = bySubject[module.Name];
        return module.IpAddress is { } ip ? byName.Concat(bySubject[ip]) : byName;
    }

    private static RoutineInfo? FirstRoutine(PlcProject project)
    {
        string? scheduled = project.Tasks.SelectMany(t => t.ScheduledPrograms).FirstOrDefault();
        ProgramInfo? program = project.Programs.FirstOrDefault(p => string.Equals(p.Name, scheduled, StringComparison.OrdinalIgnoreCase))
            ?? project.Programs.FirstOrDefault();

        if (program is not null)
        {
            return program.Routines.FirstOrDefault(r => string.Equals(r.Name, program.MainRoutineName, StringComparison.OrdinalIgnoreCase))
                ?? program.Routines.FirstOrDefault();
        }

        return project.AllRoutines.FirstOrDefault();
    }
}
