using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using LogicControl.App.Composition;
using LogicControl.Core.Analysis;
using LogicControl.Core.L5x;

namespace LogicControl.App.ViewModels;

/// <summary>
/// The System tab: the open project drawn as a system - controller, bridges, devices, and the
/// links between them - and, with more exports added, the whole plant: every controller joined on
/// its produced and consumed tags and its messages, with the findings only that join can show.
///
/// <para>The project on the other tabs is always the first; the others are opened here only. A
/// controller added here can be made the main project with one click.</para>
/// </summary>
public sealed class SystemViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private PlantModel? _plant;
    private PlantLayout _layout = PlantLayout.Empty;
    private PlantNode? _selected;
    private double _zoom = 1.0;
    private string? _error;
    private bool _busy;
    private IReadOnlyList<FindingRowViewModel> _findings = [];

    public SystemViewModel(MainViewModel main)
    {
        _main = main;
        RemoveCommand = new RelayParameterCommand(o => Remove(o as PlantProjectRowViewModel), o => o is PlantProjectRowViewModel);
        OpenNodeCommand = new RelayParameterCommand(o => OpenNode(o as PlantNode ?? _selected), o => (o as PlantNode ?? _selected) is not null);
        MakeMainCommand = new RelayParameterCommand(o => _ = MakeMainAsync(o as PlantProjectRowViewModel), o => o is PlantProjectRowViewModel);
        ZoomInCommand = new RelayCommand(() => Zoom = Math.Min(2.0, Math.Round(_zoom + 0.1, 1)));
        ZoomOutCommand = new RelayCommand(() => Zoom = Math.Max(0.3, Math.Round(_zoom - 0.1, 1)));
        ZoomResetCommand = new RelayCommand(() => Zoom = 1.0);
    }

    /// <summary>The other controllers opened beside the main project.</summary>
    public ObservableCollection<PlantProjectRowViewModel> Others { get; } = [];

    public PlantModel? Plant => _plant;

    public PlantLayout Layout
    {
        get => _layout;
        private set => SetProperty(ref _layout, value);
    }

    /// <summary>Findings that need more than one controller: a consumed tag nobody produces, a size mismatch...</summary>
    public IReadOnlyList<FindingRowViewModel> Findings
    {
        get => _findings;
        private set
        {
            if (SetProperty(ref _findings, value))
            {
                OnPropertyChanged(nameof(HasFindings));
                OnPropertyChanged(nameof(FindingsHeader));
            }
        }
    }

    public bool HasFindings => _findings.Count > 0;

    public string FindingsHeader => _findings.Count == 0
        ? (Others.Count == 0 ? "Add the controllers this one talks to, to check the links between them." : "No problems between these controllers.")
        : $"{_findings.Count.ToString(CultureInfo.InvariantCulture)} problem{(_findings.Count == 1 ? string.Empty : "s")} between controllers";

    public string Summary
    {
        get
        {
            if (_plant is null)
            {
                return string.Empty;
            }

            int controllers = _plant.Nodes.Count(n => n.Kind == PlantNodeKind.Controller);
            int devices = _plant.Nodes.Count(n => n.Kind is PlantNodeKind.Device or PlantNodeKind.Bridge);
            int crossing = _plant.Links.Count(l => l.Kind is PlantLinkKind.Produced or PlantLinkKind.Message);
            return $"{Count(controllers, "controller")} · {Count(devices, "module")} · {Count(crossing, "produced tag or message", "produced tags and messages")}";
        }
    }

    /// <summary>The box clicked last, for the details line.</summary>
    public PlantNode? SelectedNode
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                OnPropertyChanged(nameof(SelectedDetails));
                OpenNodeCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>What the selected box is and everything that joins it, one per line.</summary>
    public string SelectedDetails
    {
        get
        {
            if (_selected is not { } n || _plant is null)
            {
                return "Click a box for what it is and how it is connected; double-click to open it.";
            }

            var lines = new List<string>
            {
                string.Join("  ·  ", new[] { n.Name, n.Catalog, n.Address, n.Inhibited ? "inhibited" : null, Owner(n) }.Where(s => !string.IsNullOrEmpty(s))),
            };

            foreach (PlantLink l in _plant.Links.Where(l => (l.From == n.Id || l.To == n.Id) && l.Kind is not PlantLinkKind.Tree))
            {
                string other = l.From == n.Id ? l.To : l.From;
                string arrow = l.From == n.Id ? "→" : "←";
                lines.Add($"{arrow} {NameOf(other)}: {l.Label}{(l.Detail is null ? string.Empty : $" ({l.Detail})")}");
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>Scale of the drawing: 0.3 to 2.</summary>
    public double Zoom
    {
        get => _zoom;
        set
        {
            if (SetProperty(ref _zoom, Math.Clamp(value, 0.3, 2.0)))
            {
                OnPropertyChanged(nameof(ZoomText));
            }
        }
    }

    public string ZoomText => $"{(_zoom * 100).ToString("0", CultureInfo.InvariantCulture)}%";

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

    public bool IsBusy
    {
        get => _busy;
        private set => SetProperty(ref _busy, value);
    }

    public RelayParameterCommand RemoveCommand { get; }

    /// <summary>Opens what a box is: a module on the Hardware tab, another controller as the main project.</summary>
    public RelayParameterCommand OpenNodeCommand { get; }

    public RelayParameterCommand MakeMainCommand { get; }

    public RelayCommand ZoomInCommand { get; }

    public RelayCommand ZoomOutCommand { get; }

    public RelayCommand ZoomResetCommand { get; }

    /// <summary>Opens another controller's export beside the main one. Never throws.</summary>
    public async Task AddAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Error = null;

        if (SamePath(path, _main.FilePath) || Others.Any(o => SamePath(o.Path, path)))
        {
            Error = $"{Path.GetFileName(path)} is already open.";
            return;
        }

        IsBusy = true;
        try
        {
            ProjectAnalysis analysis = await Task.Run(() => ProjectAnalysis.Open(path)).ConfigureAwait(true);
            if (!string.Equals(analysis.Project.TargetType, "Controller", StringComparison.OrdinalIgnoreCase))
            {
                Error = $"{Path.GetFileName(path)} is a {analysis.Project.TargetType} export. The system view needs whole-controller exports.";
                return;
            }

            Add(analysis);
            _main.Recent.Add(path, RecentKind.Export);
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
            IsBusy = false;
        }
    }

    /// <summary>Adds an analysis that is already built - what <see cref="AddAsync"/> ends with.</summary>
    public void Add(ProjectAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        Others.Add(new PlantProjectRowViewModel(analysis));
        Rebuild();
    }

    public void Remove(PlantProjectRowViewModel? row)
    {
        if (row is not null && Others.Remove(row))
        {
            Rebuild();
        }
    }

    /// <summary>Joins the main project and the others again - after any of them changes.</summary>
    public void Rebuild()
    {
        var projects = new List<ProjectAnalysis>();
        if (_main.Analysis is { } main)
        {
            projects.Add(main);
        }

        projects.AddRange(Others.Select(o => o.Analysis));

        _plant = projects.Count == 0 ? null : PlantModel.Build(projects);
        Layout = _plant is null ? PlantLayout.Empty : PlantLayout.Build(_plant);
        Findings = _plant?.Findings.Select(f => new FindingRowViewModel(f)).ToList() ?? [];
        SelectedNode = null;
        OnPropertyChanged(nameof(Plant));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(FindingsHeader));
        OnPropertyChanged(nameof(SelectedDetails));
    }

    private void OpenNode(PlantNode? node)
    {
        if (node is null)
        {
            return;
        }

        if (node.Project == 0 && node.Hardware is { } hardware && node.Kind != PlantNodeKind.Controller)
        {
            _main.ShowModule(hardware);
            return;
        }

        if (node.Project == 0 && node.Kind == PlantNodeKind.Controller)
        {
            _main.SelectedTab = MainViewModel.OverviewTab;
            return;
        }

        int project = node.PeerOf ?? node.Project;
        if (project > 0 && project - 1 < Others.Count)
        {
            _ = MakeMainAsync(Others[project - 1]);
        }
    }

    /// <summary>Swaps a controller in as the main project; the old main one stays in the system view.</summary>
    private async Task MakeMainAsync(PlantProjectRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        ProjectAnalysis? previous = _main.Analysis;
        int at = Others.IndexOf(row);
        Others.Remove(row);
        PlantProjectRowViewModel? demoted = previous is null ? null : new PlantProjectRowViewModel(previous);
        if (demoted is not null)
        {
            Others.Insert(0, demoted);
        }

        await _main.OpenAsync(row.Path).ConfigureAwait(true);

        if (!ReferenceEquals(_main.Analysis, previous))
        {
            return;
        }

        // The open failed (the main window says why): put the list back as it was.
        if (demoted is not null)
        {
            Others.Remove(demoted);
        }

        Others.Insert(Math.Clamp(at, 0, Others.Count), row);
        Rebuild();
    }

    private string NameOf(string id) => _plant?.Find(id) is { } n
        ? (n.Kind == PlantNodeKind.Controller ? n.Name : $"{n.Name}{(Owner(n) is { } o ? $" ({o})" : string.Empty)}")
        : id;

    private string? Owner(PlantNode n) =>
        n.Project >= 0 && _plant is not null && n.Kind != PlantNodeKind.Controller && _plant.Projects.Count > 1
            ? $"in {_plant.Projects[n.Project].Project.Controller.Name}"
            : null;

    private static bool SamePath(string? a, string? b) =>
        a is not null && b is not null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static string Count(int n, string one, string? many = null) =>
        $"{n.ToString(CultureInfo.InvariantCulture)} {(n == 1 ? one : many ?? one + "s")}";
}

/// <summary>One controller opened beside the main project.</summary>
public sealed class PlantProjectRowViewModel(ProjectAnalysis analysis)
{
    public ProjectAnalysis Analysis { get; } = analysis;

    public string Name => Analysis.Project.Controller.Name;

    public string Path => Analysis.Project.SourcePath;

    public string Detail => $"{Analysis.Project.Controller.ProcessorType} · {System.IO.Path.GetFileName(Analysis.Project.SourcePath)}";
}
