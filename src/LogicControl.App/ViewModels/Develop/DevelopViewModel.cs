using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using LogicControl.App.Composition;
using LogicControl.Core.Authoring;
using LogicControl.Core.Authoring.History;
using LogicControl.Core.Logic;
using LogicControl.Core.Model;

namespace LogicControl.App.ViewModels.Develop;

/// <summary>
/// The Develop tab: the drafts being written, the editor for the one selected, what is wrong with
/// them, and the ways out - import files for Studio 5000, a merged copy of the open project, or a
/// .lcdev file to carry on with later.
///
/// <para>Like the rest of the view models, free of WPF: every file dialog is the window's job, and
/// what comes back here is a path.</para>
///
/// <para>Editors change the drafts in place and call <see cref="Changed"/>, which re-runs
/// <see cref="DraftChecker"/> over the whole set. That is cheap - a set is tens of items, not
/// thousands - and it means every issue in the list is always current, including the ones a
/// rename in one draft causes in another.</para>
///
/// <para><b>Every step is a revision</b> (<see cref="RevisionHistory"/>). Adding, deleting,
/// generating, a change from Claude, a revert - each is recorded as it happens. Typing is
/// recorded in bursts: edits to one draft are one revision until the person moves to another
/// draft, a minute passes, or anything else is recorded, so the history reads "Edited Routine
/// MainProgram/Motors" once rather than once per keystroke.</para>
/// </summary>
public sealed class DevelopViewModel : ObservableObject
{
    private DevelopmentSet _set = new();
    private PlcProject? _project;
    private DraftContext _context;
    private DraftItemViewModel? _selected;
    private object? _editor;
    private string? _path;
    private bool _dirty;
    private string? _message;
    private IReadOnlyList<DraftIssueRowViewModel> _issues = [];
    private bool _suspend;
    private RevisionHistory _history;
    private string? _pendingLabel;
    private DateTime _pendingSince;

    /// <summary>Typing into one draft for longer than this starts a new revision.</summary>
    internal static readonly TimeSpan EditBurst = TimeSpan.FromMinutes(1);

    public DevelopViewModel()
    {
        _context = new DraftContext(_set);
        _history = new RevisionHistory(_set);
        _history.Record(_set, "Started a development set", RevisionAuthor.File, Clock());

        NewDataTypeCommand = new RelayCommand(NewDataType);
        NewAoiCommand = new RelayCommand(NewAoi);
        NewProgramCommand = new RelayCommand(NewProgram);
        NewRoutineCommand = new RelayCommand(NewRoutine);
        EditTagsCommand = new RelayCommand(EditTags);
        FromTemplateCommand = new RelayCommand(OpenTemplates);
        DeleteCommand = new RelayCommand(DeleteSelected, () => _selected?.Draft is not null);
        ClearCommand = new RelayCommand(Clear, () => !_set.IsEmpty);
        ShowIssueCommand = new RelayParameterCommand(o => ShowIssue(o as DraftIssueRowViewModel));
        NewModuleCommand = new RelayCommand(NewModule);
        ShowHistoryCommand = new RelayCommand(() => ShowHistory(HistoryMode.Step));
        ShowProjectChangesCommand = new RelayCommand(() => ShowHistory(HistoryMode.Project), () => _project is not null && !_set.IsEmpty);
    }

    // ------------------------------------------------------------------ history

    /// <summary>The time a revision is stamped with. A test sets it; the app leaves it.</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>Every revision of the set, oldest first.</summary>
    public RevisionHistory History => _history;

    /// <summary>Raised after a revision is recorded or the set is replaced - the history view and the Logic tab follow it.</summary>
    public event EventHandler? Revised;

    /// <summary>Opens the history in the editor area: every step, what it changed, revert to any.</summary>
    public RelayCommand ShowHistoryCommand { get; }

    /// <summary>Opens what the drafts change in the open project, item by item.</summary>
    public RelayCommand ShowProjectChangesCommand { get; }

    public void ShowHistory(HistoryMode mode)
    {
        CommitPending();
        SelectedItem = null;
        Editor = new HistoryViewModel(this, mode);
    }

    /// <summary>
    /// Records the set as it stands. Anything typed before it is recorded first, under its own
    /// label, so a revision never mixes a person's edit with what came after.
    /// </summary>
    public RevisionRecord? Commit(string label, string author = RevisionAuthor.You, int? revertedTo = null)
    {
        if (author != RevisionAuthor.You || _pendingLabel is not null && _pendingLabel != label)
        {
            CommitPending();
        }

        _pendingLabel = null;
        RevisionRecord? record = _history.Record(_set, label, author, Clock(), revertedTo);
        if (record is not null)
        {
            OnRevised();
        }

        return record;
    }

    private void OnRevised()
    {
        (Editor as HistoryViewModel)?.Refresh();
        Revised?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Records edits typed since the last revision, if any.</summary>
    public void CommitPending()
    {
        if (_pendingLabel is { } label)
        {
            _pendingLabel = null;
            if (_history.Record(_set, label, RevisionAuthor.You, Clock()) is not null)
            {
                OnRevised();
            }
        }
    }

    /// <summary>
    /// Puts the drafts back exactly as they were after <paramref name="revision"/>, as a new
    /// revision - so the revert can itself be undone. The draft that was open stays open if it
    /// still exists.
    /// </summary>
    public void RevertTo(RevisionRecord revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        CommitPending();

        DraftKind? kind = _selected?.Kind;
        string? name = _selected?.Name;
        bool showingHistory = Editor is HistoryViewModel;

        DevelopmentSet restored = RevisionHistory.Restore(revision);
        restored.History = _set.History;
        _set = restored;
        _history = new RevisionHistory(_set);

        Rebuild(null);
        if (!showingHistory && Items.FirstOrDefault(i => i.Kind == kind && Same(i.Name, name)) is { } again)
        {
            SelectedItem = again;
        }

        OnPropertyChanged(nameof(ControllerName));
        OnPropertyChanged(nameof(SoftwareRevision));
        Commit($"Reverted to #{revision.Number.ToString(CultureInfo.InvariantCulture)} ({revision.Label})", RevisionAuthor.You, revision.Number);
        Message = $"Back to revision {revision.Number.ToString(CultureInfo.InvariantCulture)}. The revert is itself a revision - undo it from the history.";
    }

    /// <summary>What every draft changes in the open project: "before" is the project's own version.</summary>
    public ChangeSet ProjectChanges() =>
        _project is null ? ChangeSet.None : SetDiff.Compare(DraftsFromProject.Counterparts(_project, _set), _set);

    /// <summary>"Routine MainProgram/Motors" for the draft being edited - the label of a typed revision.</summary>
    private string DescribeSelected() => _selected?.Draft switch
    {
        UdtDraft d => $"Edited data type {d.Name}",
        AoiDraft a => $"Edited Add-On {a.Name}",
        ProgramDraft p => $"Edited program {p.Name}",
        RoutineDraft r => $"Edited routine {r.QualifiedName}",
        ModuleDraft m => $"Edited module {m.Name}",
        List<TagDraft> => "Edited tags",
        _ => "Edited drafts",
    };

    // ------------------------------------------------------------------ state

    public DevelopmentSet Set => _set;

    public PlcProject? Project => _project;

    public ObservableCollection<DraftItemViewModel> Items { get; } = [];

    public DraftItemViewModel? SelectedItem
    {
        get => _selected;
        set
        {
            if (!ReferenceEquals(_selected, value))
            {
                CommitPending();
            }

            if (SetProperty(ref _selected, value))
            {
                Editor = value is null ? null : CreateEditor(value);
                DeleteCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>The editor on the right: a UDT, AOI, program, routine, tag list or template editor.</summary>
    public object? Editor
    {
        get => _editor;
        private set => SetProperty(ref _editor, value);
    }

    public IReadOnlyList<DraftIssueRowViewModel> Issues
    {
        get => _issues;
        private set => SetProperty(ref _issues, value);
    }

    public int ErrorCount => _issues.Count(i => i.Level == Level.Error);

    public int WarningCount => _issues.Count(i => i.Level == Level.Warning);

    public string IssueSummary => _set.IsEmpty
        ? "Nothing drafted yet."
        : ErrorCount == 0 && WarningCount == 0
            ? "No problems found."
            : $"{Plural(ErrorCount, "error")}, {Plural(WarningCount, "warning")}"
              + (ErrorCount > 0 ? " - errors must be fixed before exporting." : ".");

    /// <summary>Exports are allowed: something drafted and nothing Studio 5000 would refuse.</summary>
    public bool CanExport => !_set.IsEmpty && ErrorCount == 0;

    /// <summary>Merging needs a whole-controller export open as well.</summary>
    public bool CanMerge => CanExport && string.Equals(_project?.TargetType, "Controller", StringComparison.OrdinalIgnoreCase);

    public string MergeHint => _project is null
        ? "Open a whole-controller L5X export to write drafts into a copy of it."
        : CanMerge || !CanExport ? "Write the drafts into a copy of the open project, to open in Studio 5000."
        : $"The open file is a {_project.TargetType} export; merging needs a whole-controller export.";

    public bool IsEmpty => _set.IsEmpty;

    public bool IsDirty
    {
        get => _dirty;
        private set
        {
            if (SetProperty(ref _dirty, value))
            {
                OnPropertyChanged(nameof(Title));
            }
        }
    }

    /// <summary>Where the set was last saved or opened from.</summary>
    public string? FilePath => _path;

    public string Title => (_path is null ? "Unsaved development set" : Path.GetFileName(_path)) + (_dirty ? " *" : string.Empty);

    /// <summary>The last thing an export, merge or save said, for the line under the toolbar.</summary>
    public string? Message
    {
        get => _message;
        set
        {
            if (SetProperty(ref _message, value))
            {
                OnPropertyChanged(nameof(HasMessage));
            }
        }
    }

    public bool HasMessage => !string.IsNullOrEmpty(_message);

    public string ControllerName
    {
        get => _set.ControllerName;
        set
        {
            if (!string.Equals(_set.ControllerName, value, StringComparison.Ordinal))
            {
                _set.ControllerName = value ?? string.Empty;
                OnPropertyChanged();
                Changed();
            }
        }
    }

    public string SoftwareRevision
    {
        get => _set.SoftwareRevision;
        set
        {
            if (!string.Equals(_set.SoftwareRevision, value, StringComparison.Ordinal))
            {
                _set.SoftwareRevision = value ?? string.Empty;
                OnPropertyChanged();
                Changed();
            }
        }
    }

    /// <summary>Every type a draft can use: built-in, then UDTs and AOIs from the project and the drafts.</summary>
    public IReadOnlyList<string> TypeChoices =>
        [.. LogixTypes.BuiltInTypes, .. _context.UserTypes.Concat(_context.AoiNames).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];

    public IReadOnlyList<string> ProgramChoices =>
        [.. _context.ProgramNames.Order(StringComparer.OrdinalIgnoreCase)];

    public IReadOnlyList<string> TaskChoices =>
        [.. (_project?.Tasks.Select(t => t.Name) ?? []).Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>How a rung preview draws each instruction, AOI drafts included.</summary>
    public Func<string, InstructionShape?> Shapes => _context.ShapeOf;

    internal DraftContext Context => _context;

    public RelayCommand NewDataTypeCommand { get; }

    public RelayCommand NewAoiCommand { get; }

    public RelayCommand NewProgramCommand { get; }

    public RelayCommand NewRoutineCommand { get; }

    public RelayCommand EditTagsCommand { get; }

    public RelayCommand FromTemplateCommand { get; }

    public RelayCommand DeleteCommand { get; }

    public RelayCommand ClearCommand { get; }

    public RelayParameterCommand ShowIssueCommand { get; }

    public RelayCommand NewModuleCommand { get; }

    /// <summary>Ethernet bridges in the open project a module can hang off: name and Ethernet port.</summary>
    public IReadOnlyList<string> ParentChoices =>
        [.. (_project?.Modules.Where(m => m.Ports.Any(p => p.IsEthernet && !p.Upstream) || (m.IsLocal && m.Ports.Any(p => p.IsEthernet)))
              .Select(m => m.Name) ?? ["Local"])
            .Concat(_set.Modules.Select(m => m.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    // ------------------------------------------------------------------ project

    /// <summary>
    /// Points the drafts at the project that was just opened: its types, tags and AOIs become
    /// visible to the checks, and a set that has not been touched takes its controller name and
    /// Studio 5000 version so the import files match.
    /// </summary>
    public void AttachProject(PlcProject? project)
    {
        _project = project;
        if (project is not null && !_dirty && _set.IsEmpty)
        {
            _set.ControllerName = project.Controller.Name;
            if (VersionOf(project) is { } version)
            {
                _set.SoftwareRevision = version;
            }

            OnPropertyChanged(nameof(ControllerName));
            OnPropertyChanged(nameof(SoftwareRevision));
        }

        Recheck();
    }

    /// <summary>"33.00" from the export's SoftwareRevision, or from the controller's major revision.</summary>
    internal static string? VersionOf(PlcProject project)
    {
        if (project.SoftwareRevision is { Length: > 0 } s)
        {
            return s;
        }

        return project.Controller.MajorRevision is int major
            ? $"{major.ToString(CultureInfo.InvariantCulture)}.00"
            : null;
    }

    /// <summary>
    /// Starts a routine draft from a routine in the open project - its rungs and comments copied -
    /// so it can be changed and imported back over the original.
    /// </summary>
    public void EditCopyOf(RoutineInfo routine)
    {
        ArgumentNullException.ThrowIfNull(routine);
        CommitPending();

        if (routine.OwnerIsAoi)
        {
            AoiInfo? aoi = _project?.AddOnInstructions.FirstOrDefault(a => Same(a.Name, routine.Owner));
            if (aoi is not null)
            {
                EditCopyOf(aoi);
                return;
            }
        }

        RoutineDraft draft = DraftsFromProject.Routine(routine);

        int existing = _set.Routines.FindIndex(r => Same(r.QualifiedName, draft.QualifiedName));
        if (existing >= 0)
        {
            Select(_set.Routines[existing]);
            Message = $"{draft.QualifiedName} is already being edited.";
            return;
        }

        _set.Routines.Add(draft);
        Rebuild(draft);
        Commit($"Started editing routine {draft.QualifiedName}");
        Message = $"Editing {draft.QualifiedName}. Changes show on the Logic tab against the project; export it, or write a project copy, to take it to Studio 5000.";
    }

    /// <summary>The routine draft that edits <paramref name="routine"/> in place, if there is one.</summary>
    public RoutineDraft? DraftOf(RoutineInfo routine)
    {
        ArgumentNullException.ThrowIfNull(routine);
        return routine.OwnerIsAoi ? null : _set.Routines.FirstOrDefault(r => Same(r.QualifiedName, routine.QualifiedName));
    }

    /// <summary>
    /// The rungs that edit <paramref name="routine"/> in place: a routine draft of the same name,
    /// or for an AOI's Logic routine, the AOI draft's logic. Null when it is not being edited.
    /// </summary>
    public IReadOnlyList<RungDraft>? EditOf(RoutineInfo routine)
    {
        ArgumentNullException.ThrowIfNull(routine);
        if (routine.OwnerIsAoi)
        {
            return Same(routine.Name, "Logic")
                ? _set.AddOnInstructions.FirstOrDefault(a => Same(a.Name, routine.Owner))?.Logic
                : null;
        }

        return DraftOf(routine)?.Rungs;
    }

    /// <summary>Opens the draft that edits <paramref name="routine"/> at a rung, starting one if needed.</summary>
    public void Edit(RoutineInfo routine, int? rung = null)
    {
        ArgumentNullException.ThrowIfNull(routine);
        if (EditOf(routine) is null)
        {
            EditCopyOf(routine);
        }
        else
        {
            object? draft = routine.OwnerIsAoi
                ? _set.AddOnInstructions.FirstOrDefault(a => Same(a.Name, routine.Owner))
                : DraftOf(routine);
            if (draft is not null)
            {
                Select(draft);
            }
        }

        if (rung is int at && Editor is IRungHost host)
        {
            host.Rungs.Focus(at);
        }
    }

    /// <summary>Drops the edits to a project routine: its draft goes, and the Logic tab shows the project again.</summary>
    public void DiscardEdits(RoutineInfo routine)
    {
        ArgumentNullException.ThrowIfNull(routine);
        if (routine.OwnerIsAoi && Same(routine.Name, "Logic")
            && _set.AddOnInstructions.FirstOrDefault(a => Same(a.Name, routine.Owner)) is { } aoi)
        {
            CommitPending();
            _set.AddOnInstructions.Remove(aoi);
            Rebuild(null);
            Commit($"Discarded edits to Add-On {aoi.Name}");
            Message = $"Discarded the edits to {aoi.Name}. They are still in the history.";
            return;
        }

        if (DraftOf(routine) is { } draft)
        {
            CommitPending();
            _set.Routines.Remove(draft);
            Rebuild(null);
            Commit($"Discarded edits to routine {draft.QualifiedName}");
            Message = $"Discarded the edits to {draft.QualifiedName}. They are still in the history.";
        }
    }

    /// <summary>Starts an AOI draft from one in the open project.</summary>
    public void EditCopyOf(AoiInfo aoi)
    {
        ArgumentNullException.ThrowIfNull(aoi);
        CommitPending();

        if (aoi.IsProtected)
        {
            Message = $"{aoi.Name} is source-protected; its logic is not in the export.";
            return;
        }

        AoiDraft draft = DraftsFromProject.Aoi(aoi);

        _set.AddOnInstructions.RemoveAll(a => Same(a.Name, draft.Name));
        _set.AddOnInstructions.Add(draft);
        Rebuild(draft);
        Commit($"Started editing Add-On {draft.Name}");
        Message = $"Editing a copy of the Add-On {aoi.Name}. Bump its revision before importing it back.";
    }

    /// <summary>A UDT draft from one in the open project.</summary>
    public void EditCopyOf(DataTypeInfo type)
    {
        ArgumentNullException.ThrowIfNull(type);
        CommitPending();

        UdtDraft draft = DraftsFromProject.DataType(type);

        _set.DataTypes.RemoveAll(d => Same(d.Name, draft.Name));
        _set.DataTypes.Add(draft);
        Rebuild(draft);
        Commit($"Started editing data type {draft.Name}");
    }

    /// <summary>A module draft from a Generic Ethernet module in the open project.</summary>
    public bool EditCopyOf(ModuleInfo module)
    {
        ArgumentNullException.ThrowIfNull(module);
        CommitPending();
        if (DraftsFromProject.Module(module) is not { } draft)
        {
            Message = $"{module.Name} is a {module.CatalogNumber}. Only Generic Ethernet modules can be drafted.";
            return false;
        }

        _set.Modules.RemoveAll(m => Same(m.Name, draft.Name));
        _set.Modules.Add(draft);
        Rebuild(draft);
        Commit($"Started editing module {draft.Name}");
        return true;
    }

    // ------------------------------------------------------------------ files

    public void Load(DevelopmentSet set, string? path)
    {
        ArgumentNullException.ThrowIfNull(set);
        _pendingLabel = null;
        _set = set;
        _history = new RevisionHistory(set);
        _path = path;
        IsDirty = false;
        SelectedItem = null;
        Rebuild(null);
        _history.Record(set, path is null ? "Started a development set" : $"Opened {Path.GetFileName(path)}", RevisionAuthor.File, Clock());
        OnRevised();
        IsDirty = false;
        OnPropertyChanged(nameof(ControllerName));
        OnPropertyChanged(nameof(SoftwareRevision));
        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(Title));
        ClearCommand.NotifyCanExecuteChanged();
    }

    public void Open(string path)
    {
        Load(DevelopmentSet.Load(path), path);
        Message = $"Opened {Path.GetFileName(path)}.";
    }

    public void Save(string path)
    {
        CommitPending();
        _set.Save(path);
        _path = path;
        IsDirty = false;
        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(Title));
        Message = $"Saved {Path.GetFileName(path)}.";
    }

    /// <summary>Writes one import file per item into <paramref name="folder"/>. Returns the paths written.</summary>
    public IReadOnlyList<string> ExportImportFiles(string folder)
    {
        if (!CanExport)
        {
            throw new InvalidOperationException("Fix the errors in the list before exporting.");
        }

        CommitPending();
        Directory.CreateDirectory(folder);
        var written = new List<string>();
        foreach ((string name, System.Xml.Linq.XDocument doc) in L5xWriter.ExportAll(_set))
        {
            string path = Path.Combine(folder, name);
            L5xWriter.Save(doc, path);
            written.Add(path);
        }

        Message = $"Wrote {Plural(written.Count, "import file")} to {folder}. In Studio 5000, import them in number order: "
            + "data types and AOIs from the Controller Organizer, programs on a task, routines on their program."
            + (_set.Modules.Count > 0 ? " Modules are not import files - use Write into project copy for those." : string.Empty);
        return written;
    }

    /// <summary>Writes the drafts into a copy of the open project at <paramref name="outputPath"/>.</summary>
    public MergeReport MergeInto(string outputPath)
    {
        if (!CanMerge || _project is null)
        {
            throw new InvalidOperationException(MergeHint);
        }

        CommitPending();
        MergeReport report = ProjectMerger.Merge(_project.SourcePath, _set, outputPath);
        Message = $"Wrote {Path.GetFileName(outputPath)}. Open it in Studio 5000 (File > Open, type L5X) to make a new .ACD. "
            + report.ToString().Replace(Environment.NewLine, " ", StringComparison.Ordinal);
        return report;
    }

    // ------------------------------------------------------------------ editing

    /// <summary>Called by every editor after it changes a draft.</summary>
    public void Changed()
    {
        if (_suspend)
        {
            return;
        }

        string label = DescribeSelected();
        DateTime now = Clock();
        if (_pendingLabel is not null && (_pendingLabel != label || now - _pendingSince > EditBurst))
        {
            CommitPending();
        }

        if (_pendingLabel is null)
        {
            _pendingLabel = label;
            _pendingSince = now;
        }

        IsDirty = true;
        Recheck();
        foreach (DraftItemViewModel item in Items)
        {
            item.Refresh();
        }

        ClearCommand.NotifyCanExecuteChanged();
        Edited?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised on every change to the drafts, typed or not - the Logic tab redraws an edited routine from it.</summary>
    public event EventHandler? Edited;

    /// <summary>Adds a generated set - from a template - and opens its first routine.</summary>
    public void Add(DevelopmentSet generated)
    {
        ArgumentNullException.ThrowIfNull(generated);
        CommitPending();
        _set.Merge(generated);
        object? first = (object?)generated.Routines.FirstOrDefault() ?? (object?)generated.AddOnInstructions.FirstOrDefault() ?? generated.DataTypes.FirstOrDefault();
        Rebuild(first);
        IsDirty = true;
        Commit("Generated from a template", RevisionAuthor.Template);
        Message = "Generated "
            + string.Join(", ", new[]
            {
                Count(generated.DataTypes.Count, "data type"),
                Count(generated.AddOnInstructions.Count, "Add-On"),
                Count(generated.Programs.Count, "program"),
                Count(generated.Tags.Count, "tag"),
                Count(generated.Routines.Sum(r => r.Rungs.Count), "rung"),
            }.Where(s => s.Length > 0))
            + ".";
    }

    /// <summary>
    /// The set was changed from outside the editors - by the assistant's draft tools. Rebuilds the
    /// list, keeps the same item selected if it still exists (by kind and name, because a tool
    /// replaces a draft with a new object), or selects <paramref name="focus"/>.
    /// </summary>
    public void ChangedElsewhere(object? focus, string? message = null, string? label = null, string author = RevisionAuthor.Claude)
    {
        DraftKind? kind = _selected?.Kind;
        string? name = _selected?.Name;

        // The change has already been made, so anything typed before it cannot get a revision of
        // its own any more - the assistant panel records it before Claude starts (CommitPending).
        _pendingLabel = null;

        Rebuild(null);
        IsDirty = true;
        Commit(label ?? message ?? "Changed outside the editors", author);

        DraftItemViewModel? again = Items.FirstOrDefault(i => i.Kind == kind && Same(i.Name, name));
        if (focus is not null)
        {
            Select(focus);
        }
        else if (again is not null)
        {
            SelectedItem = again;
        }

        if (message is not null)
        {
            Message = message;
        }
    }

    public void Select(object draft) =>
        SelectedItem = Items.FirstOrDefault(i => ReferenceEquals(i.Draft, draft))
            ?? (draft is TagDraft ? Items.FirstOrDefault(i => i.Kind == DraftKind.Tags) : null);

    private void NewDataType()
    {
        CommitPending();
        var udt = new UdtDraft { Name = UniqueName("NewType", _set.DataTypes.Select(d => d.Name)), Members = [new("Value", "DINT")] };
        _set.DataTypes.Add(udt);
        Rebuild(udt);
        Commit($"Added data type {udt.Name}");
    }

    private void NewAoi()
    {
        CommitPending();
        var aoi = new AoiDraft
        {
            Name = UniqueName("NewAoi", _set.AddOnInstructions.Select(a => a.Name)),
            Parameters = [new("In", "BOOL", "Input", true), new("Out", "BOOL", "Output", false)],
            Logic = [new("XIC(In)OTE(Out);")],
        };
        _set.AddOnInstructions.Add(aoi);
        Rebuild(aoi);
        Commit($"Added Add-On {aoi.Name}");
    }

    private void NewProgram()
    {
        CommitPending();
        var program = new ProgramDraft { Name = UniqueName("NewProgram", ProgramChoices), MainRoutineName = "MainRoutine" };
        _set.Programs.Add(program);
        _set.Routines.Add(new RoutineDraft { Name = "MainRoutine", Program = program.Name, Rungs = [new("NOP();")] });
        Rebuild(program);
        Commit($"Added program {program.Name}");
    }

    private void NewRoutine()
    {
        CommitPending();
        string program = (_selected?.Draft as RoutineDraft)?.Program
            ?? (_selected?.Draft as ProgramDraft)?.Name
            ?? ProgramChoices.FirstOrDefault()
            ?? "MainProgram";

        var routine = new RoutineDraft
        {
            Name = UniqueName("NewRoutine", _set.Routines.Where(r => Same(r.Program, program)).Select(r => r.Name)),
            Program = program,
            Rungs = [new("XIC(?)OTE(?);")],
        };
        _set.Routines.Add(routine);
        Rebuild(routine);
        Commit($"Added routine {routine.QualifiedName}");
    }

    private void NewModule()
    {
        CommitPending();
        string parent = ParentChoices.FirstOrDefault(p => !Same(p, "Local")) ?? ParentChoices.FirstOrDefault() ?? "Local";
        var module = new ModuleDraft
        {
            Name = UniqueName("NewModule", _set.Modules.Select(m => m.Name).Concat(_project?.Modules.Select(m => m.Name) ?? [])),
            ParentModule = parent,
            ParentPortId = _project?.Modules.FirstOrDefault(m => Same(m.Name, parent))?.Ports.FirstOrDefault(p => p.IsEthernet)?.Id ?? 2,
            IpAddress = SuggestAddress(parent),
        };
        _set.Modules.Add(module);
        Rebuild(module);
        Commit($"Added module {module.Name}");
    }

    /// <summary>The next free address after the highest one on the parent's network, as a starting point.</summary>
    private string SuggestAddress(string parent)
    {
        var used = (_project?.Modules.Select(m => m.IpAddress) ?? []).Concat(_set.Modules.Select(m => m.IpAddress))
            .Where(ip => ip is not null && System.Net.IPAddress.TryParse(ip, out _) && ip.Count(c => c == '.') == 3)
            .Select(ip => ip!.Split('.').Select(int.Parse).ToArray())
            .ToList();

        string? bridgeIp = _project?.Modules.FirstOrDefault(m => Same(m.Name, parent))?.Ports
            .FirstOrDefault(p => p.IsEthernet && !string.IsNullOrEmpty(p.Address))?.Address;
        if (bridgeIp is null || bridgeIp.Count(c => c == '.') != 3)
        {
            return "192.168.1.100";
        }

        int[] net = bridgeIp.Split('.').Select(int.Parse).ToArray();
        int highest = used.Where(u => u[0] == net[0] && u[1] == net[1] && u[2] == net[2]).Select(u => u[3]).DefaultIfEmpty(net[3]).Max();
        return $"{net[0]}.{net[1]}.{net[2]}.{Math.Min(254, highest + 1)}";
    }

    private void Clear()
    {
        CommitPending();
        _set = new DevelopmentSet { ControllerName = _set.ControllerName, SoftwareRevision = _set.SoftwareRevision, History = _set.History };
        _history = new RevisionHistory(_set);
        SelectedItem = null;
        Rebuild(null);
        Commit("Cleared every draft");
        Message = "Cleared. Every draft is still in the history if you want one back.";
    }

    private void EditTags() => SelectedItem = Items.FirstOrDefault(i => i.Kind == DraftKind.Tags);

    private void OpenTemplates()
    {
        SelectedItem = null;
        Editor = new TemplateGeneratorViewModel(this);
    }

    private void DeleteSelected()
    {
        CommitPending();
        switch (_selected?.Draft)
        {
            case UdtDraft d:
                _set.DataTypes.Remove(d);
                break;
            case AoiDraft a:
                _set.AddOnInstructions.Remove(a);
                break;
            case ProgramDraft p:
                _set.Programs.Remove(p);
                _set.Routines.RemoveAll(r => Same(r.Program, p.Name));
                _set.Tags.RemoveAll(t => Same(t.Program, p.Name));
                break;
            case RoutineDraft r:
                _set.Routines.Remove(r);
                break;
            case ModuleDraft m:
                _set.Modules.Remove(m);
                break;
            default:
                return;
        }

        string what = _selected!.Name;
        string kind = _selected.Kind switch
        {
            DraftKind.DataType => "data type",
            DraftKind.Aoi => "Add-On",
            DraftKind.Program => "program",
            DraftKind.Module => "module",
            _ => "routine",
        };
        Rebuild(null);
        IsDirty = true;
        Commit($"Deleted {kind} {what}");
    }

    private void ShowIssue(DraftIssueRowViewModel? row)
    {
        if (row?.Issue.Target is { } target)
        {
            Select(target);
            if (row.Issue.Rung is int rung && Editor is IRungHost host)
            {
                host.Rungs.Focus(rung);
            }
        }
    }

    // ------------------------------------------------------------------ internals

    /// <summary>Rebuilds the item list from the set, then selects <paramref name="select"/>.</summary>
    private void Rebuild(object? select)
    {
        _suspend = true;
        try
        {
            Items.Clear();
            foreach (UdtDraft d in _set.DataTypes)
            {
                Items.Add(new DraftItemViewModel(DraftKind.DataType, d));
            }

            foreach (AoiDraft a in _set.AddOnInstructions)
            {
                Items.Add(new DraftItemViewModel(DraftKind.Aoi, a));
            }

            foreach (ProgramDraft p in _set.Programs)
            {
                Items.Add(new DraftItemViewModel(DraftKind.Program, p));
            }

            foreach (RoutineDraft r in _set.Routines)
            {
                Items.Add(new DraftItemViewModel(DraftKind.Routine, r));
            }

            foreach (ModuleDraft m in _set.Modules)
            {
                Items.Add(new DraftItemViewModel(DraftKind.Module, m));
            }

            Items.Add(new DraftItemViewModel(DraftKind.Tags, _set.Tags));
        }
        finally
        {
            _suspend = false;
        }

        Recheck();
        if (select is not null)
        {
            Select(select);
        }

        IsDirty = true;
        OnPropertyChanged(nameof(IsEmpty));
        ClearCommand.NotifyCanExecuteChanged();
    }

    private void Recheck()
    {
        _context = new DraftContext(_set, _project);
        IReadOnlyList<DraftIssue> issues = DraftChecker.Check(_set, _project);
        Issues = issues
            .OrderByDescending(i => i.IsError)
            .Select(i => new DraftIssueRowViewModel(i))
            .ToList();

        ILookup<object, DraftIssue> byTarget = issues
            .Where(i => i.Target is not null)
            .ToLookup(i => i.Target is TagDraft ? _set.Tags : i.Target!, ReferenceEqualityComparer.Instance);

        foreach (DraftItemViewModel item in Items)
        {
            List<DraftIssue> mine = item.Draft is null ? [] : byTarget[item.Draft].ToList();
            item.Level = mine.Any(i => i.IsError) ? Level.Error : mine.Count > 0 ? Level.Warning : Level.None;
        }

        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(IssueSummary));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(CanMerge));
        OnPropertyChanged(nameof(MergeHint));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(TypeChoices));
        OnPropertyChanged(nameof(ProgramChoices));
        OnPropertyChanged(nameof(TaskChoices));
        OnPropertyChanged(nameof(ParentChoices));
        OnPropertyChanged(nameof(Shapes));
        ShowProjectChangesCommand?.NotifyCanExecuteChanged();
    }

    private object CreateEditor(DraftItemViewModel item) => item.Draft switch
    {
        UdtDraft d => new UdtEditorViewModel(this, d),
        AoiDraft a => new AoiEditorViewModel(this, a),
        ProgramDraft p => new ProgramEditorViewModel(this, p),
        RoutineDraft r => new RoutineEditorViewModel(this, r),
        ModuleDraft m => new ModuleEditorViewModel(this, m),
        List<TagDraft> => new TagsEditorViewModel(this),
        _ => new TemplateGeneratorViewModel(this),
    };

    internal static string UniqueName(string stem, IEnumerable<string> taken)
    {
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(stem))
        {
            return stem;
        }

        for (int i = 2; ; i++)
        {
            string candidate = stem + i.ToString(CultureInfo.InvariantCulture);
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    internal static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Plural(int n, string what) => $"{n.ToString(CultureInfo.InvariantCulture)} {what}{(n == 1 ? string.Empty : "s")}";

    private static string Count(int n, string what) => n == 0 ? string.Empty : Plural(n, what);
}

public enum DraftKind
{
    DataType,
    Aoi,
    Program,
    Routine,
    Module,
    Tags,
}

/// <summary>One row in the drafts list: what it is, its name, and whether anything is wrong with it.</summary>
public sealed class DraftItemViewModel(DraftKind kind, object draft) : ObservableObject
{
    private string _level = ViewModels.Level.None;

    public DraftKind Kind { get; } = kind;

    public object Draft { get; } = draft;

    /// <summary>The list's group header.</summary>
    public string Group => Kind switch
    {
        DraftKind.DataType => "DATA TYPES",
        DraftKind.Aoi => "ADD-ON INSTRUCTIONS",
        DraftKind.Program => "PROGRAMS",
        DraftKind.Routine => "ROUTINES",
        DraftKind.Module => "MODULES",
        _ => "TAGS",
    };

    public string Name => Draft switch
    {
        UdtDraft d => Blank(d.Name),
        AoiDraft a => Blank(a.Name),
        ProgramDraft p => Blank(p.Name),
        RoutineDraft r => $"{r.Program}/{Blank(r.Name)}",
        ModuleDraft m => Blank(m.Name),
        List<TagDraft> => "Tags",
        _ => string.Empty,
    };

    public string Detail => Draft switch
    {
        UdtDraft d => $"{d.Members.Count} members",
        AoiDraft a => $"{a.Parameters.Count} parameters · {a.Logic.Count} rungs",
        ProgramDraft p => p.Task is { Length: > 0 } t ? $"in {t}" : "unscheduled",
        RoutineDraft r => $"{r.Rungs.Count} rungs",
        ModuleDraft m => $"{m.IpAddress} under {m.ParentModule}",
        List<TagDraft> tags => $"{tags.Count} drafted",
        _ => string.Empty,
    };

    public string Level
    {
        get => _level;
        set => SetProperty(ref _level, value);
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Detail));
    }

    private static string Blank(string name) => name.Length == 0 ? "(unnamed)" : name;
}

/// <summary>An issue in the list under the editor.</summary>
public sealed class DraftIssueRowViewModel(DraftIssue issue)
{
    public DraftIssue Issue { get; } = issue;

    public string Level => Issue.IsError ? ViewModels.Level.Error : ViewModels.Level.Warning;

    public string Where => Issue.Location;

    public string Message => Issue.Message;
}
