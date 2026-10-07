using System.Collections.ObjectModel;
using System.Globalization;
using LogicControl.App.Composition;
using LogicControl.Core.Authoring;

namespace LogicControl.App.ViewModels.Develop;

// The editors on the right of the Develop tab, one per kind of draft. Each edits its draft in
// place and tells DevelopViewModel.Changed() so the checks and the list stay current. Grid rows
// are small wrappers over the draft's own objects, so a cell edit is a property set and nothing
// has to be copied back.

/// <summary>A UDT: its name, description and members, with a paste box for a whole member list.</summary>
public sealed class UdtEditorViewModel : ObservableObject
{
    private readonly DevelopViewModel _owner;
    private readonly UdtDraft _udt;
    private MemberRowViewModel? _selected;
    private string _pasteText = string.Empty;
    private string? _pasteProblems;

    public UdtEditorViewModel(DevelopViewModel owner, UdtDraft udt)
    {
        _owner = owner;
        _udt = udt;
        foreach (MemberDraft m in udt.Members)
        {
            Members.Add(new MemberRowViewModel(this, m));
        }

        _pasteText = DeclarationText.FormatMembers(udt.Members);

        AddCommand = new RelayCommand(Add);
        RemoveCommand = new RelayCommand(Remove, () => _selected is not null);
        MoveUpCommand = new RelayCommand(() => Move(-1), () => _selected is not null && Members.IndexOf(_selected) > 0);
        MoveDownCommand = new RelayCommand(() => Move(+1), () => _selected is not null && Members.IndexOf(_selected) < Members.Count - 1);
        ApplyPasteCommand = new RelayCommand(ApplyPaste);
    }

    public string Heading => "Data type";

    public string Name
    {
        get => _udt.Name;
        set => Set(() => _udt.Name = value?.Trim() ?? string.Empty, _udt.Name, value);
    }

    public string? Description
    {
        get => _udt.Description;
        set => Set(() => _udt.Description = value, _udt.Description, value);
    }

    public ObservableCollection<MemberRowViewModel> Members { get; } = [];

    public MemberRowViewModel? SelectedMember
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                RemoveCommand.NotifyCanExecuteChanged();
                MoveUpCommand.NotifyCanExecuteChanged();
                MoveDownCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public IReadOnlyList<string> TypeChoices => _owner.TypeChoices;

    /// <summary>The members as text - "Name : TYPE // description" per line - for pasting a list in.</summary>
    public string PasteText
    {
        get => _pasteText;
        set => SetProperty(ref _pasteText, value ?? string.Empty);
    }

    public string? PasteProblems
    {
        get => _pasteProblems;
        private set => SetProperty(ref _pasteProblems, value);
    }

    public RelayCommand AddCommand { get; }

    public RelayCommand RemoveCommand { get; }

    public RelayCommand MoveUpCommand { get; }

    public RelayCommand MoveDownCommand { get; }

    /// <summary>Replaces the member list with what the paste box holds.</summary>
    public RelayCommand ApplyPasteCommand { get; }

    internal void Changed() => _owner.Changed();

    private void Set(Action apply, string? before, string? after)
    {
        if (!string.Equals(before, after, StringComparison.Ordinal))
        {
            apply();
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(Description));
            _owner.Changed();
        }
    }

    private void Add()
    {
        var member = new MemberDraft(DevelopViewModel.UniqueName("Member", _udt.Members.Select(m => m.Name)), "DINT");
        int at = _selected is null ? _udt.Members.Count : Members.IndexOf(_selected) + 1;
        _udt.Members.Insert(at, member);
        var row = new MemberRowViewModel(this, member);
        Members.Insert(at, row);
        SelectedMember = row;
        _owner.Changed();
    }

    private void Remove()
    {
        if (_selected is null)
        {
            return;
        }

        _udt.Members.Remove(_selected.Draft);
        Members.Remove(_selected);
        SelectedMember = null;
        _owner.Changed();
    }

    private void Move(int by)
    {
        if (_selected is null)
        {
            return;
        }

        int from = Members.IndexOf(_selected);
        int to = from + by;
        MemberRowViewModel row = _selected;
        Members.Move(from, to);
        _udt.Members.RemoveAt(from);
        _udt.Members.Insert(to, row.Draft);
        SelectedMember = row;
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        _owner.Changed();
    }

    private void ApplyPaste()
    {
        (List<MemberDraft> members, List<string> problems) = DeclarationText.ParseMembers(_pasteText);
        PasteProblems = problems.Count == 0 ? null : string.Join(Environment.NewLine, problems);
        if (members.Count == 0)
        {
            return;
        }

        _udt.Members.Clear();
        _udt.Members.AddRange(members);
        Members.Clear();
        foreach (MemberDraft m in members)
        {
            Members.Add(new MemberRowViewModel(this, m));
        }

        _owner.Changed();
    }
}

public sealed class MemberRowViewModel(UdtEditorViewModel editor, MemberDraft draft) : ObservableObject
{
    public MemberDraft Draft { get; } = draft;

    public string Name
    {
        get => Draft.Name;
        set => Edit(() => Draft.Name = value?.Trim() ?? string.Empty);
    }

    public string DataType
    {
        get => Draft.DataType;
        set => Edit(() => Draft.DataType = LogixTypes.Canonical(value?.Trim() ?? string.Empty));
    }

    public int Dimension
    {
        get => Draft.Dimension;
        set => Edit(() => Draft.Dimension = Math.Max(0, value));
    }

    public string? Description
    {
        get => Draft.Description;
        set => Edit(() => Draft.Description = string.IsNullOrWhiteSpace(value) ? null : value);
    }

    private void Edit(Action apply)
    {
        apply();
        OnPropertyChanged(string.Empty);
        editor.Changed();
    }
}

/// <summary>A ladder routine: where it goes and its rungs.</summary>
public sealed class RoutineEditorViewModel : ObservableObject, IRungHost
{
    private readonly DevelopViewModel _owner;
    private readonly RoutineDraft _routine;

    public RoutineEditorViewModel(DevelopViewModel owner, RoutineDraft routine)
    {
        _owner = owner;
        _routine = routine;
        Rungs = new RungListViewModel(owner, routine.Rungs, () => RungScope.ForProgram(owner.Context, _routine.Program));
    }

    public string Heading => "Ladder routine";

    public string Name
    {
        get => _routine.Name;
        set => Edit(() => _routine.Name = value?.Trim() ?? string.Empty);
    }

    public string Program
    {
        get => _routine.Program;
        set => Edit(() => _routine.Program = value?.Trim() ?? string.Empty, recheckRungs: true);
    }

    public string? Description
    {
        get => _routine.Description;
        set => Edit(() => _routine.Description = string.IsNullOrWhiteSpace(value) ? null : value);
    }

    public IReadOnlyList<string> ProgramChoices => _owner.ProgramChoices;

    public RungListViewModel Rungs { get; }

    private void Edit(Action apply, bool recheckRungs = false)
    {
        apply();
        OnPropertyChanged(string.Empty);
        _owner.Changed();
        if (recheckRungs)
        {
            foreach (RungEditorViewModel r in Rungs.Items)
            {
                r.Recheck();
            }
        }
    }
}

/// <summary>A program: name, description, main routine and the task to schedule it in.</summary>
public sealed class ProgramEditorViewModel(DevelopViewModel owner, ProgramDraft program) : ObservableObject
{
    public string Heading => "Program";

    public string Name
    {
        get => program.Name;
        set
        {
            string old = program.Name;
            string name = value?.Trim() ?? string.Empty;
            if (string.Equals(old, name, StringComparison.Ordinal))
            {
                return;
            }

            // Routines and tags drafted in this program move with it.
            program.Name = name;
            foreach (RoutineDraft r in owner.Set.Routines.Where(r => DevelopViewModel.Same(r.Program, old)))
            {
                r.Program = name;
            }

            foreach (TagDraft t in owner.Set.Tags.Where(t => DevelopViewModel.Same(t.Program, old)))
            {
                t.Program = name;
            }

            OnPropertyChanged(string.Empty);
            owner.Changed();
        }
    }

    public string? Description
    {
        get => program.Description;
        set => Edit(() => program.Description = string.IsNullOrWhiteSpace(value) ? null : value);
    }

    public string? MainRoutineName
    {
        get => program.MainRoutineName;
        set => Edit(() => program.MainRoutineName = string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    public string? Task
    {
        get => program.Task;
        set => Edit(() => program.Task = string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    public IReadOnlyList<string> RoutineChoices =>
        [.. owner.Set.Routines.Where(r => DevelopViewModel.Same(r.Program, program.Name)).Select(r => r.Name)];

    public IReadOnlyList<string> TaskChoices => owner.TaskChoices;

    private void Edit(Action apply)
    {
        apply();
        OnPropertyChanged(string.Empty);
        owner.Changed();
    }
}

/// <summary>An Add-On Instruction: interface, local tags and Logic.</summary>
public sealed class AoiEditorViewModel : ObservableObject, IRungHost
{
    private readonly DevelopViewModel _owner;
    private readonly AoiDraft _aoi;
    private AoiParameterRowViewModel? _selectedParameter;
    private AoiLocalRowViewModel? _selectedLocal;

    public AoiEditorViewModel(DevelopViewModel owner, AoiDraft aoi)
    {
        _owner = owner;
        _aoi = aoi;

        foreach (AoiParameterDraft p in aoi.Parameters)
        {
            Parameters.Add(new AoiParameterRowViewModel(this, p));
        }

        foreach (AoiLocalTagDraft l in aoi.LocalTags)
        {
            LocalTags.Add(new AoiLocalRowViewModel(this, l));
        }

        Rungs = new RungListViewModel(owner, aoi.Logic, () => RungScope.ForAoi(owner.Context, _aoi));

        AddParameterCommand = new RelayCommand(() =>
        {
            var p = new AoiParameterDraft(DevelopViewModel.UniqueName("Param", Names()), "BOOL", "Input", false);
            _aoi.Parameters.Add(p);
            Parameters.Add(new AoiParameterRowViewModel(this, p));
            Changed();
        });
        RemoveParameterCommand = new RelayCommand(() =>
        {
            if (_selectedParameter is { } row)
            {
                _aoi.Parameters.Remove(row.Draft);
                Parameters.Remove(row);
                Changed();
            }
        });
        AddLocalCommand = new RelayCommand(() =>
        {
            var l = new AoiLocalTagDraft(DevelopViewModel.UniqueName("Local", Names()), "DINT");
            _aoi.LocalTags.Add(l);
            LocalTags.Add(new AoiLocalRowViewModel(this, l));
            Changed();
        });
        RemoveLocalCommand = new RelayCommand(() =>
        {
            if (_selectedLocal is { } row)
            {
                _aoi.LocalTags.Remove(row.Draft);
                LocalTags.Remove(row);
                Changed();
            }
        });
    }

    public string Heading => "Add-On Instruction";

    public string Name
    {
        get => _aoi.Name;
        set => Edit(() => _aoi.Name = value?.Trim() ?? string.Empty);
    }

    public string Revision
    {
        get => _aoi.Revision;
        set => Edit(() => _aoi.Revision = string.IsNullOrWhiteSpace(value) ? "1.0" : value.Trim());
    }

    public string? Description
    {
        get => _aoi.Description;
        set => Edit(() => _aoi.Description = string.IsNullOrWhiteSpace(value) ? null : value);
    }

    /// <summary>How a call looks in ladder: the instance, then the required parameters.</summary>
    public string CallSignature =>
        $"{_aoi.Name}(Instance{string.Concat(_aoi.Parameters.Where(p => p.IsRequired).Select(p => "," + p.Name))})";

    public ObservableCollection<AoiParameterRowViewModel> Parameters { get; } = [];

    public ObservableCollection<AoiLocalRowViewModel> LocalTags { get; } = [];

    public AoiParameterRowViewModel? SelectedParameter
    {
        get => _selectedParameter;
        set => SetProperty(ref _selectedParameter, value);
    }

    public AoiLocalRowViewModel? SelectedLocal
    {
        get => _selectedLocal;
        set => SetProperty(ref _selectedLocal, value);
    }

    public IReadOnlyList<string> TypeChoices => _owner.TypeChoices;

    public IReadOnlyList<string> UsageChoices { get; } = ["Input", "Output", "InOut"];

    public RungListViewModel Rungs { get; }

    public RelayCommand AddParameterCommand { get; }

    public RelayCommand RemoveParameterCommand { get; }

    public RelayCommand AddLocalCommand { get; }

    public RelayCommand RemoveLocalCommand { get; }

    internal void Changed()
    {
        OnPropertyChanged(nameof(CallSignature));
        _owner.Changed();
        foreach (RungEditorViewModel r in Rungs.Items)
        {
            r.Recheck();
        }
    }

    private IEnumerable<string> Names() => _aoi.Parameters.Select(p => p.Name).Concat(_aoi.LocalTags.Select(l => l.Name));

    private void Edit(Action apply)
    {
        apply();
        OnPropertyChanged(string.Empty);
        Changed();
    }
}

public sealed class AoiParameterRowViewModel(AoiEditorViewModel editor, AoiParameterDraft draft) : ObservableObject
{
    public AoiParameterDraft Draft { get; } = draft;

    public string Name { get => Draft.Name; set => Edit(() => Draft.Name = value?.Trim() ?? string.Empty); }

    public string DataType { get => Draft.DataType; set => Edit(() => Draft.DataType = LogixTypes.Canonical(value?.Trim() ?? string.Empty)); }

    public string Usage { get => Draft.Usage; set => Edit(() => Draft.Usage = value ?? "Input"); }

    public bool Required { get => Draft.IsRequired; set => Edit(() => Draft.Required = value); }

    public bool Visible { get => Draft.Visible || Draft.IsRequired; set => Edit(() => Draft.Visible = value); }

    public string? Description { get => Draft.Description; set => Edit(() => Draft.Description = string.IsNullOrWhiteSpace(value) ? null : value); }

    private void Edit(Action apply)
    {
        apply();
        OnPropertyChanged(string.Empty);
        editor.Changed();
    }
}

public sealed class AoiLocalRowViewModel(AoiEditorViewModel editor, AoiLocalTagDraft draft) : ObservableObject
{
    public AoiLocalTagDraft Draft { get; } = draft;

    public string Name { get => Draft.Name; set => Edit(() => Draft.Name = value?.Trim() ?? string.Empty); }

    public string DataType { get => Draft.DataType; set => Edit(() => Draft.DataType = LogixTypes.Canonical(value?.Trim() ?? string.Empty)); }

    public int Dimension { get => Draft.Dimension; set => Edit(() => Draft.Dimension = Math.Max(0, value)); }

    public string? Description { get => Draft.Description; set => Edit(() => Draft.Description = string.IsNullOrWhiteSpace(value) ? null : value); }

    private void Edit(Action apply)
    {
        apply();
        OnPropertyChanged(string.Empty);
        editor.Changed();
    }
}

/// <summary>Every drafted tag in one grid, with a paste box for a tag list.</summary>
public sealed class TagsEditorViewModel : ObservableObject
{
    public const string ControllerScope = "(controller)";

    private readonly DevelopViewModel _owner;
    private TagEditRowViewModel? _selected;
    private string _pasteText = string.Empty;
    private string _pasteScope = ControllerScope;
    private string? _pasteProblems;

    public TagsEditorViewModel(DevelopViewModel owner)
    {
        _owner = owner;
        foreach (TagDraft t in owner.Set.Tags)
        {
            Tags.Add(new TagEditRowViewModel(this, t));
        }

        AddCommand = new RelayCommand(() =>
        {
            var tag = new TagDraft(DevelopViewModel.UniqueName("NewTag", owner.Set.Tags.Select(t => t.Name)), "DINT");
            owner.Set.Tags.Add(tag);
            var row = new TagEditRowViewModel(this, tag);
            Tags.Add(row);
            SelectedTag = row;
            Changed();
        });
        RemoveCommand = new RelayCommand(() =>
        {
            if (_selected is { } row)
            {
                owner.Set.Tags.Remove(row.Draft);
                Tags.Remove(row);
                SelectedTag = null;
                Changed();
            }
        });
        ApplyPasteCommand = new RelayCommand(ApplyPaste);
    }

    public string Heading => "Tags";

    public ObservableCollection<TagEditRowViewModel> Tags { get; } = [];

    public TagEditRowViewModel? SelectedTag
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    public IReadOnlyList<string> TypeChoices => _owner.TypeChoices;

    public IReadOnlyList<string> ScopeChoices => [ControllerScope, .. _owner.ProgramChoices];

    public string PasteText
    {
        get => _pasteText;
        set => SetProperty(ref _pasteText, value ?? string.Empty);
    }

    public string PasteScope
    {
        get => _pasteScope;
        set => SetProperty(ref _pasteScope, value ?? ControllerScope);
    }

    public string? PasteProblems
    {
        get => _pasteProblems;
        private set => SetProperty(ref _pasteProblems, value);
    }

    public RelayCommand AddCommand { get; }

    public RelayCommand RemoveCommand { get; }

    /// <summary>Adds the pasted tags; a tag of the same name in the same scope is replaced.</summary>
    public RelayCommand ApplyPasteCommand { get; }

    internal void Changed() => _owner.Changed();

    private void ApplyPaste()
    {
        string? program = _pasteScope == ControllerScope ? null : _pasteScope;
        (List<TagDraft> tags, List<string> problems) = DeclarationText.ParseTags(_pasteText, program);
        PasteProblems = problems.Count == 0 ? null : string.Join(Environment.NewLine, problems);

        foreach (TagDraft tag in tags)
        {
            int existing = _owner.Set.Tags.FindIndex(t => DevelopViewModel.Same(t.QualifiedName, tag.QualifiedName));
            if (existing >= 0)
            {
                Tags.Remove(Tags.First(r => ReferenceEquals(r.Draft, _owner.Set.Tags[existing])));
                _owner.Set.Tags[existing] = tag;
            }
            else
            {
                _owner.Set.Tags.Add(tag);
            }

            Tags.Add(new TagEditRowViewModel(this, tag));
        }

        if (tags.Count > 0)
        {
            PasteText = string.Empty;
            Changed();
        }
    }
}

public sealed class TagEditRowViewModel(TagsEditorViewModel editor, TagDraft draft) : ObservableObject
{
    public TagDraft Draft { get; } = draft;

    public string Name { get => Draft.Name; set => Edit(() => Draft.Name = value?.Trim() ?? string.Empty); }

    public string Scope
    {
        get => Draft.Program ?? TagsEditorViewModel.ControllerScope;
        set => Edit(() => Draft.Program = string.IsNullOrWhiteSpace(value) || value == TagsEditorViewModel.ControllerScope ? null : value.Trim());
    }

    public string DataType { get => Draft.DataType; set => Edit(() => Draft.DataType = LogixTypes.Canonical(value?.Trim() ?? string.Empty)); }

    public string? Dimensions
    {
        get => Draft.Dimensions;
        set => Edit(() => Draft.Dimensions = string.IsNullOrWhiteSpace(value) ? null : string.Join(' ', value.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)));
    }

    public string? AliasFor { get => Draft.AliasFor; set => Edit(() => Draft.AliasFor = string.IsNullOrWhiteSpace(value) ? null : value.Trim()); }

    public string? Description { get => Draft.Description; set => Edit(() => Draft.Description = string.IsNullOrWhiteSpace(value) ? null : value); }

    private void Edit(Action apply)
    {
        apply();
        OnPropertyChanged(string.Empty);
        editor.Changed();
    }
}

/// <summary>
/// Generate from a template: pick one, name the instances, say where they go, and it writes the
/// UDT or AOI, the tags and the rungs into the set.
/// </summary>
public sealed class TemplateGeneratorViewModel : ObservableObject
{
    private readonly DevelopViewModel _owner;
    private LogicTemplate _template;
    private string _instances = "M101, M102";
    private string _typeName;
    private string _program;
    private string _routine;
    private bool _programScopeTags;
    private bool _createProgram;
    private string? _task;
    private string? _error;

    public TemplateGeneratorViewModel(DevelopViewModel owner)
    {
        _owner = owner;
        _template = LogicTemplates.All[0];
        _typeName = _template.DefaultTypeName;
        _routine = _template.DefaultTypeName + "s";
        _program = owner.ProgramChoices.FirstOrDefault() ?? "MainProgram";
        _createProgram = owner.ProgramChoices.Count == 0;
        _task = owner.TaskChoices.FirstOrDefault();
        Settings = Build(_template);
        GenerateCommand = new RelayCommand(Generate);
    }

    public string Heading => "Generate from a template";

    public IReadOnlyList<LogicTemplate> Templates => LogicTemplates.All;

    public LogicTemplate Template
    {
        get => _template;
        set
        {
            if (value is not null && SetProperty(ref _template, value))
            {
                TypeName = value.DefaultTypeName;
                Routine = value.DefaultTypeName + "s";
                Settings = Build(value);
                OnPropertyChanged(nameof(Settings));
                OnPropertyChanged(nameof(Preview));
            }
        }
    }

    /// <summary>"M101, M102" or "M101-M110".</summary>
    public string Instances
    {
        get => _instances;
        set
        {
            if (SetProperty(ref _instances, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(Preview));
            }
        }
    }

    public string TypeName
    {
        get => _typeName;
        set => SetProperty(ref _typeName, value?.Trim() ?? string.Empty);
    }

    public string Program
    {
        get => _program;
        set => SetProperty(ref _program, value?.Trim() ?? string.Empty);
    }

    public string Routine
    {
        get => _routine;
        set => SetProperty(ref _routine, value?.Trim() ?? string.Empty);
    }

    public bool ProgramScopeTags
    {
        get => _programScopeTags;
        set => SetProperty(ref _programScopeTags, value);
    }

    public bool CreateProgram
    {
        get => _createProgram;
        set => SetProperty(ref _createProgram, value);
    }

    public string? Task
    {
        get => _task;
        set => SetProperty(ref _task, value);
    }

    public IReadOnlyList<string> ProgramChoices => _owner.ProgramChoices;

    public IReadOnlyList<string> TaskChoices => _owner.TaskChoices;

    public IReadOnlyList<TemplateSettingRowViewModel> Settings { get; private set; }

    public string Preview
    {
        get
        {
            List<string> names = TemplateRequest.ParseInstances(_instances);
            return names.Count == 0
                ? "Name at least one instance."
                : $"{names.Count.ToString(CultureInfo.InvariantCulture)} instance{(names.Count == 1 ? string.Empty : "s")}: {string.Join(", ", names.Take(12))}{(names.Count > 12 ? ", ..." : string.Empty)}";
        }
    }

    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    public RelayCommand GenerateCommand { get; }

    private void Generate()
    {
        List<string> names = TemplateRequest.ParseInstances(_instances);
        if (names.Count == 0)
        {
            Error = "Name at least one instance - M101, M102 or M101-M110.";
            return;
        }

        if (LogixTypes.NameProblem(_typeName, "Type name") is { } typeProblem)
        {
            Error = typeProblem;
            return;
        }

        if (names.Select(n => LogixTypes.NameProblem(n, "Instance name")).FirstOrDefault(p => p is not null) is { } nameProblem)
        {
            Error = nameProblem;
            return;
        }

        Error = null;
        DevelopmentSet generated = _template.Generate(new TemplateRequest
        {
            Instances = names,
            TypeName = _typeName,
            Program = _program,
            RoutineName = _routine,
            ProgramScopeTags = _programScopeTags,
            CreateProgram = _createProgram,
            Task = _task,
            Values = Settings.ToDictionary(s => s.Key, s => s.Value, StringComparer.OrdinalIgnoreCase),
        });

        _owner.Add(generated);
    }

    private static List<TemplateSettingRowViewModel> Build(LogicTemplate template) =>
        template.Settings.Select(s => new TemplateSettingRowViewModel(s.Key, s.Label, s.Default)).ToList();
}

public sealed class TemplateSettingRowViewModel(string key, string label, string value) : ObservableObject
{
    private string _value = value;

    public string Key { get; } = key;

    public string Label { get; } = label;

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value ?? string.Empty);
    }
}
