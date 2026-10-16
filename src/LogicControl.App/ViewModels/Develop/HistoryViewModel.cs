using System.Collections.ObjectModel;
using System.IO;
using System.Globalization;
using LogicControl.App.Composition;
using LogicControl.Core.Authoring;
using LogicControl.Core.Authoring.History;

namespace LogicControl.App.ViewModels.Develop;

/// <summary>What the history view compares.</summary>
public enum HistoryMode
{
    /// <summary>What the selected revision did: it against the one before.</summary>
    Step,

    /// <summary>Everything since the selected revision: it against now - what reverting would undo.</summary>
    SinceThen,

    /// <summary>The drafts against the open project: what exporting or merging would change.</summary>
    Project,
}

/// <summary>
/// The history of the development set, in the Develop tab's editor area: every revision, newest
/// first, with who made it and what it changed; the selected one's changes in full, rung by rung;
/// and a button to go back to it. The same viewer shows the drafts against the open project.
/// </summary>
public sealed class HistoryViewModel : ObservableObject
{
    private readonly DevelopViewModel _owner;
    private HistoryMode _mode;
    private RevisionRowViewModel? _selected;
    private IReadOnlyList<ItemChangeViewModel> _changes = [];
    private string _caption = string.Empty;
    private bool _showLadder = true;
    private bool _sideBySide = true;
    private DiffSides _sides = DiffSides.Default;

    public HistoryViewModel(DevelopViewModel owner, HistoryMode mode)
    {
        _owner = owner;
        _mode = mode;

        RevertCommand = new RelayCommand(Revert, () => _selected is { IsLatest: false });
        RefreshCommand = new RelayCommand(Refresh);
        ShowStepCommand = new RelayCommand(() => Mode = HistoryMode.Step);
        ShowSinceCommand = new RelayCommand(() => Mode = HistoryMode.SinceThen);
        ShowProjectCommand = new RelayCommand(() => Mode = HistoryMode.Project, () => _owner.Project is not null);
        OpenDraftCommand = new RelayParameterCommand(o => OpenDraft(o as ItemChangeViewModel), o => o is ItemChangeViewModel { CanOpen: true });

        Refresh();
    }

    public string Heading => "History";

    public ObservableCollection<RevisionRowViewModel> Revisions { get; } = [];

    public RevisionRowViewModel? SelectedRevision
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                RevertCommand.NotifyCanExecuteChanged();
                if (_mode == HistoryMode.Project && value is not null)
                {
                    Mode = HistoryMode.Step;
                }
                else
                {
                    Compare();
                }
            }
        }
    }

    public HistoryMode Mode
    {
        get => _mode;
        set
        {
            if (SetProperty(ref _mode, value))
            {
                OnPropertyChanged(nameof(IsStep));
                OnPropertyChanged(nameof(IsSince));
                OnPropertyChanged(nameof(IsProject));
                Compare();
            }
        }
    }

    public bool IsStep
    {
        get => _mode == HistoryMode.Step;
        set
        {
            if (value)
            {
                Mode = HistoryMode.Step;
            }
        }
    }

    public bool IsSince
    {
        get => _mode == HistoryMode.SinceThen;
        set
        {
            if (value)
            {
                Mode = HistoryMode.SinceThen;
            }
        }
    }

    public bool IsProject
    {
        get => _mode == HistoryMode.Project;
        set
        {
            if (value)
            {
                Mode = HistoryMode.Project;
            }
        }
    }

    public bool HasProject => _owner.Project is not null;

    /// <summary>Rungs in the changes drawn as ladder (the default), or listed as neutral text.</summary>
    public bool ShowLadder
    {
        get => _showLadder;
        set
        {
            if (SetProperty(ref _showLadder, value))
            {
                OnPropertyChanged(nameof(ShowText));
                Compare();
            }
        }
    }

    public bool ShowText
    {
        get => !_showLadder;
        set => ShowLadder = !value;
    }

    /// <summary>The older version in a left pane and the newer in a right one (the default), or one list.</summary>
    public bool SideBySide
    {
        get => _sideBySide;
        set
        {
            if (SetProperty(ref _sideBySide, value))
            {
                OnPropertyChanged(nameof(Stacked));
                Compare();
            }
        }
    }

    public bool Stacked
    {
        get => !_sideBySide;
        set => SideBySide = !value;
    }

    /// <summary>What the left and right panes are: the older version and the newer.</summary>
    public DiffSides Sides
    {
        get => _sides;
        private set => SetProperty(ref _sides, value);
    }

    /// <summary>What the right-hand side is showing, in words.</summary>
    public string Caption
    {
        get => _caption;
        private set => SetProperty(ref _caption, value);
    }

    public IReadOnlyList<ItemChangeViewModel> Changes
    {
        get => _changes;
        private set
        {
            if (SetProperty(ref _changes, value))
            {
                OnPropertyChanged(nameof(HasChanges));
            }
        }
    }

    public bool HasChanges => _changes.Count > 0;

    public RelayCommand RevertCommand { get; }

    public RelayCommand RefreshCommand { get; }

    public RelayCommand ShowStepCommand { get; }

    public RelayCommand ShowSinceCommand { get; }

    public RelayCommand ShowProjectCommand { get; }

    /// <summary>Opens the draft a change is about in its editor.</summary>
    public RelayParameterCommand OpenDraftCommand { get; }

    /// <summary>Rebuilds the list from the history - after a revision, a revert or a load. Keeps the selection by number.</summary>
    public void Refresh()
    {
        int? keep = _selected?.Number;
        RevisionHistory history = _owner.History;
        int latest = history.Latest?.Number ?? 0;

        Revisions.Clear();
        foreach (RevisionRecord r in history.Revisions.Reverse())
        {
            Revisions.Add(new RevisionRowViewModel(r, r.Number == latest));
        }

        RevisionRowViewModel? again = Revisions.FirstOrDefault(r => r.Number == keep) ?? Revisions.FirstOrDefault();
        if (!ReferenceEquals(again, _selected))
        {
            _selected = again;
            OnPropertyChanged(nameof(SelectedRevision));
        }

        RevertCommand.NotifyCanExecuteChanged();
        ShowProjectCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasProject));
        Compare();
    }

    private void Compare()
    {
        ChangeSet changes;
        if (_mode == HistoryMode.Project)
        {
            changes = _owner.ProjectChanges();
            string file = _owner.Project is null ? "the project" : Path.GetFileName(_owner.Project.SourcePath);
            Sides = new DiffSides($"Project - {file}", "Drafts", "project", "drafts");
            Caption = _owner.Project is null
                ? "Open a project to compare the drafts with it."
                : changes.IsEmpty
                    ? $"The drafts change nothing in {Path.GetFileName(_owner.Project.SourcePath)}."
                    : $"What the drafts change in {Path.GetFileName(_owner.Project.SourcePath)} - green is new, red is gone.";
        }
        else if (_selected is null)
        {
            changes = ChangeSet.None;
            Sides = DiffSides.Default;
            Caption = "No revisions yet.";
        }
        else if (_mode == HistoryMode.SinceThen)
        {
            changes = RevisionHistory.Since(_selected.Record, _owner.Set);
            string at = Number(_selected.Number);
            Sides = new DiffSides($"Revision {at}", "Now", at, "now");
            Caption = changes.IsEmpty
                ? $"Nothing has changed since revision {_selected.Number.ToString(CultureInfo.InvariantCulture)}."
                : $"Everything since revision {_selected.Number.ToString(CultureInfo.InvariantCulture)} - what going back to it would undo.";
        }
        else
        {
            changes = _owner.History.ChangesIn(_selected.Record);
            string at = Number(_selected.Number);
            int index = IndexOf(_owner.History.Revisions, _selected.Record);
            string before = index > 0 ? Number(_owner.History.Revisions[index - 1].Number) : string.Empty;
            Sides = before.Length > 0
                ? new DiffSides($"Revision {before}", $"Revision {at}", before, at)
                : new DiffSides("Before the first revision", $"Revision {at}", "before", at);
            Caption = changes.IsEmpty
                ? $"Revision {_selected.Number.ToString(CultureInfo.InvariantCulture)} changed nothing in the drafts."
                : $"What revision {_selected.Number.ToString(CultureInfo.InvariantCulture)} changed: {_selected.Label}.";
        }

        Changes = changes.Items.Select(i => new ItemChangeViewModel(i, Exists(i), _showLadder, _owner.Shapes, _sides, _sideBySide)).ToList();
    }

    private static string Number(int n) => $"#{n.ToString(CultureInfo.InvariantCulture)}";

    private static int IndexOf(IReadOnlyList<RevisionRecord> records, RevisionRecord record)
    {
        for (int i = 0; i < records.Count; i++)
        {
            if (ReferenceEquals(records[i], record))
            {
                return i;
            }
        }

        return -1;
    }

    private bool Exists(ItemChange change) => _owner.Items.Any(i => i.Draft is not List<TagDraft> && Matches(i, change))
        || (change.What == "Tag" && change.Kind != ItemChangeKind.Removed);

    private static bool Matches(DraftItemViewModel item, ItemChange change) =>
        DevelopViewModel.Same(item.Name, change.Name) && change.What switch
        {
            "Data type" => item.Kind == DraftKind.DataType,
            "Add-On" => item.Kind == DraftKind.Aoi,
            "Program" => item.Kind == DraftKind.Program,
            "Routine" => item.Kind == DraftKind.Routine,
            "Module" => item.Kind == DraftKind.Module,
            _ => false,
        };

    private void OpenDraft(ItemChangeViewModel? change)
    {
        if (change is null)
        {
            return;
        }

        DraftItemViewModel? item = change.Change.What == "Tag"
            ? _owner.Items.FirstOrDefault(i => i.Kind == DraftKind.Tags)
            : _owner.Items.FirstOrDefault(i => Matches(i, change.Change));
        if (item is null)
        {
            return;
        }

        _owner.SelectedItem = item;
        if (change.Change.TouchedRungs.Count > 0 && _owner.Editor is IRungHost host)
        {
            host.Rungs.Focus(change.Change.TouchedRungs[0]);
        }
    }

    private void Revert()
    {
        if (_selected is { IsLatest: false } row)
        {
            _owner.RevertTo(row.Record);
        }
    }
}

/// <summary>One revision in the list.</summary>
public sealed class RevisionRowViewModel(RevisionRecord record, bool isLatest)
{
    public RevisionRecord Record { get; } = record;

    public int Number => Record.Number;

    public string NumberText => $"#{Record.Number.ToString(CultureInfo.InvariantCulture)}";

    public string When
    {
        get
        {
            DateTime local = Record.TimeUtc.ToLocalTime();
            return local.Date == DateTime.Now.Date
                ? local.ToString("HH:mm:ss", CultureInfo.CurrentCulture)
                : local.ToString("d MMM HH:mm", CultureInfo.CurrentCulture);
        }
    }

    public string Author => Record.Author;

    public string Label => Record.Label;

    public string Summary => Record.Summary ?? string.Empty;

    public bool IsLatest { get; } = isLatest;

    public bool IsRevert => Record.RevertedTo is not null;

    /// <summary>"Claude", "Template", "You" - the chip colour in the list.</summary>
    public string AuthorKind => Record.Author;
}

/// <summary>
/// One changed draft, with its lines - read top to bottom (<see cref="Lines"/>) or as two panes,
/// the older version on the left and the newer on the right (<see cref="Rows"/>).
/// </summary>
public sealed class ItemChangeViewModel
{
    private IReadOnlyList<DiffRowViewModel>? _rows;
    private readonly Func<string, LogicControl.Core.Logic.InstructionShape?>? _shapes;
    private readonly bool _draw;

    public ItemChangeViewModel(
        ItemChange change, bool canOpen, bool drawRungs = false, Func<string, LogicControl.Core.Logic.InstructionShape?>? shapes = null,
        DiffSides? sides = null, bool sideBySide = false)
    {
        ArgumentNullException.ThrowIfNull(change);
        Change = change;
        CanOpen = canOpen;
        Sides = sides ?? DiffSides.Default;
        SideBySide = sideBySide;
        _draw = drawRungs;
        _shapes = shapes;
        Lines = change.Lines.Select(l => new DiffLineViewModel(l, drawRungs && l.IsRung, shapes, Sides)).ToList();
    }

    public ItemChange Change { get; }

    public string Title => Change.Title;

    public string What => Change.What;

    public string Name => Change.Name;

    public string Kind => Change.Kind.ToString();

    public string KindText => Change.Kind switch
    {
        ItemChangeKind.Added => "new",
        ItemChangeKind.Removed => "removed",
        _ => "changed",
    };

    public string Summary => Change.Summary;

    /// <summary>What the left (older) and right (newer) sides are.</summary>
    public DiffSides Sides { get; }

    public string BeforeName => Sides.Before;

    public string AfterName => Sides.After;

    /// <summary>Shown as two panes (true) or one list read top to bottom.</summary>
    public bool SideBySide { get; }

    public IReadOnlyList<DiffLineViewModel> Lines { get; }

    /// <summary>The lines as rows of two panes; built the first time a view asks.</summary>
    public IReadOnlyList<DiffRowViewModel> Rows => _rows ??= DiffRows.Build(Change.Lines, _draw, _shapes);

    /// <summary>The draft still exists, so the title can open it.</summary>
    public bool CanOpen { get; }
}

/// <summary>
/// A line of a change: text, or - for a rung when the view is drawing ladder - the rung drawn,
/// with a bar and a label saying whether it was added, removed or is unchanged context.
/// </summary>
public sealed class DiffLineViewModel(DiffLine line, bool drawn, Func<string, LogicControl.Core.Logic.InstructionShape?>? shapes, DiffSides? sides = null)
{
    private readonly DiffSides _sides = sides ?? DiffSides.Default;

    public DiffLine Line { get; } = line;

    public DiffLineKind Kind => Line.Kind;

    public string Text => Line.Text;

    /// <summary>Drawn as ladder rather than printed.</summary>
    public bool IsDrawn { get; } = drawn;

    public bool IsPrinted => !IsDrawn;

    public string? Rung => Line.Rung;

    /// <summary>
    /// "+ Rung 3 (other export)", "- Rung 3 (open project)", "Rung 4" - the drawn rung's heading.
    /// An added or removed rung says which side it is from, so nobody has to guess what before
    /// and after mean.
    /// </summary>
    public string Label => Kind switch
    {
        DiffLineKind.Added => $"+ {Line.RungLabel} ({_sides.AfterShort})",
        DiffLineKind.Removed => $"- {Line.RungLabel} ({_sides.BeforeShort})",
        _ => Line.RungLabel ?? string.Empty,
    };

    public string Comment => Line.RungComment ?? string.Empty;

    public bool HasComment => !string.IsNullOrWhiteSpace(Line.RungComment);

    /// <summary>The change bar's kind, as the Logic tab uses it: Added, Removed, or empty for context.</summary>
    public string Change => Kind switch
    {
        DiffLineKind.Added => "Added",
        DiffLineKind.Removed => "Removed",
        _ => string.Empty,
    };

    public bool IsRemoved => Kind == DiffLineKind.Removed;

    public bool IsHighlighted => false;

    public Func<string, LogicControl.Core.Logic.InstructionShape?>? Shapes { get; } = shapes;
}
