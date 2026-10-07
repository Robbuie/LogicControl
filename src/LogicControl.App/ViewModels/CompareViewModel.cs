using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using LogicControl.App.Composition;
using LogicControl.App.ViewModels.Develop;
using LogicControl.Core.Analysis;
using LogicControl.Core.Authoring;
using LogicControl.Core.Authoring.History;
using LogicControl.Core.L5x;
using LogicControl.Core.Model;

namespace LogicControl.App.ViewModels;

/// <summary>
/// The Compare tab: the open project against another export - an older backup, a sister line, the
/// version a contractor sent back. Every difference item by item, the selected one drawn rung by
/// rung, any two routines against each other, the other export's version of an item taken into
/// the Develop tab as a draft, and Claude asked to review it all with the comparison in its tools.
/// </summary>
public sealed class CompareViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private ProjectAnalysis? _other;
    private bool _swapped;
    private string _scope = ProjectComparison.All;
    private bool _showLadder = true;
    private IReadOnlyList<ItemChangeViewModel> _changes = [];
    private ItemChangeViewModel? _selected;
    private string? _error;
    private bool _busy;
    private string? _thisRoutine;
    private string? _otherRoutine;
    private bool _pair;

    public CompareViewModel(MainViewModel main)
    {
        _main = main;
        SwapCommand = new RelayCommand(() => Swapped = !Swapped, () => _other is not null);
        CloseCommand = new RelayCommand(Close, () => _other is not null);
        TakeOtherCommand = new RelayCommand(TakeOther, () => CanTakeOther);
        ComparePairCommand = new RelayCommand(ComparePair, () => _other is not null && _main.Analysis is not null && _thisRoutine is not null && _otherRoutine is not null);
        ShowAllCommand = new RelayCommand(() => { _pair = false; Refresh(); }, () => _pair);
        ReviewAllCommand = new RelayCommand(() => Ask(ReviewAllPrompt()), () => _other is not null);
        ReviewSelectedCommand = new RelayCommand(() => Ask(ReviewSelectedPrompt()), () => _selected is not null);
    }

    // ------------------------------------------------------------------ state

    /// <summary>The export the open project is compared with, or null.</summary>
    public ProjectAnalysis? Other
    {
        get => _other;
        private set
        {
            if (SetProperty(ref _other, value))
            {
                OnPropertyChanged(nameof(HasOther));
                OnPropertyChanged(nameof(OtherName));
                OnPropertyChanged(nameof(Scopes));
                OnPropertyChanged(nameof(OtherRoutines));
                RaiseCommands();
            }
        }
    }

    public bool HasOther => _other is not null;

    public string OtherName => _other is null ? "Nothing to compare with yet" : $"{_other.Project.Controller.Name} ({Path.GetFileName(_other.Project.SourcePath)})";

    public string ThisName => _main.Analysis is { } a ? $"{a.Project.Controller.Name} ({Path.GetFileName(a.Project.SourcePath)})" : "(no project open)";

    /// <summary>"Open project → other export", or the reverse: how the lines read.</summary>
    public string Direction => _swapped
        ? $"Read as {OtherName} → {ThisName}: green is only in the open project."
        : $"Read as {ThisName} → {OtherName}: green is only in the other export.";

    /// <summary>Read the other export as the starting point instead.</summary>
    public bool Swapped
    {
        get => _swapped;
        set
        {
            if (SetProperty(ref _swapped, value))
            {
                OnPropertyChanged(nameof(Direction));
                Refresh();
            }
        }
    }

    public IReadOnlyList<ScopeChoice> Scopes =>
        _other is null || _main.Analysis is null
            ? []
            : ProjectComparison.Scopes(_main.Analysis.Project, _other.Project).Select(s => new ScopeChoice(s)).ToList();

    public string Scope
    {
        get => _scope;
        set
        {
            if (SetProperty(ref _scope, value ?? ProjectComparison.All))
            {
                _pair = false;
                Refresh();
            }
        }
    }

    public bool ShowLadder
    {
        get => _showLadder;
        set
        {
            if (SetProperty(ref _showLadder, value))
            {
                OnPropertyChanged(nameof(ShowText));
                Refresh();
            }
        }
    }

    public bool ShowText
    {
        get => !_showLadder;
        set => ShowLadder = !value;
    }

    /// <summary>Every difference in scope - or the one routine pair being compared.</summary>
    public IReadOnlyList<ItemChangeViewModel> Changes
    {
        get => _changes;
        private set
        {
            if (SetProperty(ref _changes, value))
            {
                OnPropertyChanged(nameof(Summary));
                OnPropertyChanged(nameof(IsIdentical));
            }
        }
    }

    public ItemChangeViewModel? SelectedChange
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(TakeOtherText));
                RaiseCommands();
            }
        }
    }

    public bool HasSelection => _selected is not null;

    public string Summary => _other is null
        ? "Pick another L5X export to compare the open project with."
        : _pair
            ? "Comparing two routines."
            : _changes.Count == 0
                ? "No differences" + (_scope.Length > 0 ? $" in {new ScopeChoice(_scope).Label}." : ".")
                : $"{_changes.Count.ToString(CultureInfo.InvariantCulture)} difference{(_changes.Count == 1 ? string.Empty : "s")}"
                  + (_scope.Length > 0 ? $" in {new ScopeChoice(_scope).Label}" : string.Empty) + ".";

    public bool IsIdentical => _other is not null && _changes.Count == 0;

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

    // ------------------------------------------------------------------ two routines

    public IReadOnlyList<string> ThisRoutines => _main.Analysis?.Project.AllRoutines.Select(r => r.QualifiedName).ToList() ?? [];

    public IReadOnlyList<string> OtherRoutines => _other?.Project.AllRoutines.Select(r => r.QualifiedName).ToList() ?? [];

    public string? ThisRoutine
    {
        get => _thisRoutine;
        set
        {
            if (SetProperty(ref _thisRoutine, value))
            {
                ComparePairCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string? OtherRoutine
    {
        get => _otherRoutine;
        set
        {
            if (SetProperty(ref _otherRoutine, value))
            {
                ComparePairCommand.NotifyCanExecuteChanged();
            }
        }
    }

    // ------------------------------------------------------------------ commands

    public RelayCommand SwapCommand { get; }

    public RelayCommand CloseCommand { get; }

    /// <summary>Takes the other export's version of the selected item into the Develop tab as a draft.</summary>
    public RelayCommand TakeOtherCommand { get; }

    public RelayCommand ComparePairCommand { get; }

    public RelayCommand ShowAllCommand { get; }

    /// <summary>Asks Claude to review every difference.</summary>
    public RelayCommand ReviewAllCommand { get; }

    /// <summary>Asks Claude about the selected difference.</summary>
    public RelayCommand ReviewSelectedCommand { get; }

    public bool CanTakeOther => _selected is not null && _other is not null && OtherDraftOf(_selected.Change) is not null;

    public string TakeOtherText => _selected is null ? "Use the other version"
        : _selected.Change.Kind == ItemChangeKind.Removed && !_swapped || _selected.Change.Kind == ItemChangeKind.Added && _swapped
            ? "Only in the open project"
            : "Use the other export's version";

    // ------------------------------------------------------------------ actions

    /// <summary>Reads the other export off the UI thread and compares. Never throws.</summary>
    public async Task OpenAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Error = null;
        IsBusy = true;
        try
        {
            ProjectAnalysis other = await Task.Run(() => ProjectAnalysis.Open(path)).ConfigureAwait(true);
            Load(other);
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

    /// <summary>Compares with an export that is already read - what <see cref="OpenAsync"/> ends with.</summary>
    public void Load(ProjectAnalysis other)
    {
        ArgumentNullException.ThrowIfNull(other);
        Other = other;
        _scope = ProjectComparison.All;
        _pair = false;
        OnPropertyChanged(nameof(Scope));
        OnPropertyChanged(nameof(ThisName));
        OnPropertyChanged(nameof(Direction));
        Refresh();
    }

    public void Close()
    {
        Other = null;
        _pair = false;
        Changes = [];
        SelectedChange = null;
        OnPropertyChanged(nameof(Direction));
    }

    /// <summary>Compares again - after the open project or a setting changes.</summary>
    public void Refresh()
    {
        string? keep = _selected?.Title;
        OnPropertyChanged(nameof(ThisName));
        OnPropertyChanged(nameof(ThisRoutines));
        OnPropertyChanged(nameof(Direction));

        if (_other is null || _main.Analysis is null)
        {
            Changes = [];
            SelectedChange = null;
            RaiseCommands();
            return;
        }

        IEnumerable<ItemChange> items = _pair && Pair() is { } pair
            ? [pair]
            : (_swapped
                ? ProjectComparison.Compare(_other.Project, _main.Analysis.Project, _scope)
                : ProjectComparison.Compare(_main.Analysis.Project, _other.Project, _scope)).Items;

        Changes = items.Select(i => new ItemChangeViewModel(i, canOpen: false, _showLadder, _main.Develop.Shapes)).ToList();
        SelectedChange = _changes.FirstOrDefault(c => c.Title == keep) ?? _changes.FirstOrDefault();
        OnPropertyChanged(nameof(Summary));
        RaiseCommands();
    }

    private ItemChange? Pair()
    {
        RoutineInfo? mine = _main.Analysis?.Project.AllRoutines.FirstOrDefault(r => r.QualifiedName == _thisRoutine);
        RoutineInfo? theirs = _other?.Project.AllRoutines.FirstOrDefault(r => r.QualifiedName == _otherRoutine);
        return mine is null || theirs is null ? null
            : _swapped ? ProjectComparison.CompareRoutines(theirs, mine) : ProjectComparison.CompareRoutines(mine, theirs);
    }

    private void ComparePair()
    {
        _pair = true;
        Refresh();
    }

    /// <summary>
    /// The other export's version of an item as a draft for the open project. A routine compared
    /// against one of another name is drafted under the open project's name, so it imports over it.
    /// </summary>
    internal object? OtherDraftOf(ItemChange change)
    {
        if (_other is null)
        {
            return null;
        }

        PlcProject other = _other.Project;
        bool onlyMine = _swapped ? change.Kind == ItemChangeKind.Added : change.Kind == ItemChangeKind.Removed;
        if (onlyMine)
        {
            return null; // nothing in the other export to take
        }

        static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        switch (change.What)
        {
            case "Data type":
                return other.DataTypes.FirstOrDefault(d => Same(d.Name, change.Name)) is { } d ? DraftsFromProject.DataType(d) : null;
            case "Add-On":
                return other.AddOnInstructions.FirstOrDefault(a => Same(a.Name, change.Name)) is { IsProtected: false } a ? DraftsFromProject.Aoi(a) : null;
            case "Program":
                return other.Programs.FirstOrDefault(p => Same(p.Name, change.Name)) is { } p ? DraftsFromProject.Program(p, other) : null;
            case "Tag":
                return other.AllTags.FirstOrDefault(t => Same(t.QualifiedName, change.Name)) is { } t && !t.IsMessage && t.Kind is TagKind.Base or TagKind.Alias
                    ? DraftsFromProject.Tag(t)
                    : null;
            case "Module":
                return other.Modules.FirstOrDefault(m => Same(m.Name, change.Name)) is { } m ? DraftsFromProject.Module(m) : null;
            case "Routine":
            {
                string theirs = _pair && _otherRoutine is not null ? _otherRoutine : change.Name;
                RoutineInfo? routine = other.AllRoutines.FirstOrDefault(r => Same(r.QualifiedName, theirs));
                if (routine is null || routine.Language != RoutineLanguage.Ladder || routine.IsProtected || routine.OwnerIsAoi)
                {
                    return null;
                }

                RoutineDraft draft = DraftsFromProject.Routine(routine);
                if (_pair && _thisRoutine is { } mine && mine.IndexOf('/', StringComparison.Ordinal) is int slash and > 0)
                {
                    draft.Program = mine[..slash];
                    draft.Name = mine[(slash + 1)..];
                }

                return draft;
            }

            default:
                return null;
        }
    }

    private void TakeOther()
    {
        if (_selected is null || OtherDraftOf(_selected.Change) is not { } draft)
        {
            return;
        }

        string from = _other is null ? "the other export" : Path.GetFileName(_other.Project.SourcePath);
        _main.Develop.Adopt(draft, $"Took {_selected.Change.Title} from {from}");
        _main.StartDeveloping();
    }

    private void Ask(string prompt)
    {
        if (_main.Assistant.AskCommand.CanExecute(prompt))
        {
            _main.Assistant.AskCommand.Execute(prompt);
        }
        else
        {
            _main.Assistant.IsOpen = true;
            _main.Assistant.Input = prompt;
        }
    }

    internal string ReviewAllPrompt() =>
        $"Review the differences between the open project and {OtherName}"
        + (_scope.Length > 0 ? $", in {new ScopeChoice(_scope).Label}" : string.Empty)
        + ". Use compare_summary and compare_item. Tell me which differences matter - behaviour changes, risks, anything that looks unintended - and what you would keep from each side.";

    internal string ReviewSelectedPrompt() =>
        _selected is null ? string.Empty
        : $"Review the difference in {_selected.Title} between the open project and {OtherName} (compare_item). What changed in behaviour, is it right, and should the open project take it?";

    private void RaiseCommands()
    {
        SwapCommand.NotifyCanExecuteChanged();
        CloseCommand.NotifyCanExecuteChanged();
        TakeOtherCommand.NotifyCanExecuteChanged();
        ComparePairCommand.NotifyCanExecuteChanged();
        ShowAllCommand.NotifyCanExecuteChanged();
        ReviewAllCommand.NotifyCanExecuteChanged();
        ReviewSelectedCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanTakeOther));
        OnPropertyChanged(nameof(TakeOtherText));
    }
}

/// <summary>A scope for the comparison, with the words the drop-down shows.</summary>
public sealed record ScopeChoice(string Value)
{
    public string Label => Value switch
    {
        ProjectComparison.All => "Everything",
        ProjectComparison.ControllerScope => "Controller scope",
        _ => $"Program {Value}",
    };

    public override string ToString() => Label;
}
