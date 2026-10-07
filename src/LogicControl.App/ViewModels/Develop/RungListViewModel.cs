using System.Collections.ObjectModel;
using System.Globalization;
using LogicControl.App.Composition;
using LogicControl.Core.Authoring;
using LogicControl.Core.Logic;

namespace LogicControl.App.ViewModels.Develop;

/// <summary>An editor with a rung list - a routine, or an AOI's Logic.</summary>
public interface IRungHost
{
    RungListViewModel Rungs { get; }
}

/// <summary>
/// The rung editor shared by routines and AOIs: a list of rungs, each a text box with its ladder
/// drawn under it as it is typed and its problems listed beside it.
///
/// <para>Edits go straight into the draft's <see cref="RungDraft"/> list, so the list here and the
/// draft never disagree; adding, removing and moving keep both in step.</para>
/// </summary>
public sealed class RungListViewModel : ObservableObject
{
    private readonly DevelopViewModel _owner;
    private readonly List<RungDraft> _rungs;
    private readonly Func<RungScope> _scope;
    private RungEditorViewModel? _selected;

    public RungListViewModel(DevelopViewModel owner, List<RungDraft> rungs, Func<RungScope> scope)
    {
        _owner = owner;
        _rungs = rungs;
        _scope = scope;

        foreach (RungDraft r in rungs)
        {
            Items.Add(new RungEditorViewModel(this, r));
        }

        Renumber();

        AddCommand = new RelayCommand(() => Insert(_selected is null ? Items.Count : Items.IndexOf(_selected) + 1, new RungDraft("XIC(?)OTE(?);")));
        DuplicateCommand = new RelayCommand(
            () => Insert(Items.IndexOf(_selected!) + 1, new RungDraft(_selected!.Text, _selected.Comment)),
            () => _selected is not null);
        RemoveCommand = new RelayParameterCommand(o => Remove(o as RungEditorViewModel ?? _selected), o => (o ?? _selected) is RungEditorViewModel);
        MoveUpCommand = new RelayParameterCommand(o => Move(o as RungEditorViewModel ?? _selected, -1), o => (o ?? _selected) is RungEditorViewModel r && Items.IndexOf(r) > 0);
        MoveDownCommand = new RelayParameterCommand(o => Move(o as RungEditorViewModel ?? _selected, +1), o => (o ?? _selected) is RungEditorViewModel r && Items.IndexOf(r) < Items.Count - 1);
        InsertSnippetCommand = new RelayParameterCommand(o => InsertSnippet(o as string), o => o is string);
    }

    public ObservableCollection<RungEditorViewModel> Items { get; } = [];

    public RungEditorViewModel? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                DuplicateCommand.NotifyCanExecuteChanged();
                RemoveCommand.NotifyCanExecuteChanged();
                MoveUpCommand.NotifyCanExecuteChanged();
                MoveDownCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string Summary => $"{Items.Count.ToString(CultureInfo.InvariantCulture)} rung{(Items.Count == 1 ? string.Empty : "s")}";

    /// <summary>Common shapes for the quick-insert row: a contact, a coil, a branch, a timer...</summary>
    public IReadOnlyList<SnippetViewModel> Snippets { get; } =
    [
        new("-| |-", "XIC(?)", "Examine if closed"),
        new("-|/|-", "XIO(?)", "Examine if open"),
        new("-( )-", "OTE(?)", "Output energize"),
        new("-(L)-", "OTL(?)", "Output latch"),
        new("-(U)-", "OTU(?)", "Output unlatch"),
        new("[ , ]", "[XIC(?),XIC(?)]", "Branch with two legs"),
        new("TON", "TON(?,1000,0)", "Timer on delay"),
        new("CTU", "CTU(?,10,0)", "Count up"),
        new("ONS", "ONS(?)", "One shot"),
        new("MOV", "MOV(?,?)", "Move"),
        new("EQU", "EQU(?,?)", "Equal"),
        new("GRT", "GRT(?,?)", "Greater than"),
        new("RES", "RES(?)", "Reset"),
        new("JSR", "JSR(?,0)", "Jump to subroutine"),
    ];

    public Func<string, InstructionShape?> Shapes => _owner.Shapes;

    public RelayCommand AddCommand { get; }

    public RelayCommand DuplicateCommand { get; }

    public RelayParameterCommand RemoveCommand { get; }

    public RelayParameterCommand MoveUpCommand { get; }

    public RelayParameterCommand MoveDownCommand { get; }

    /// <summary>Appends a snippet to the selected rung, before its output if it is a condition.</summary>
    public RelayParameterCommand InsertSnippetCommand { get; }

    /// <summary>Selects rung <paramref name="index"/> - from a click in the issue list.</summary>
    public void Focus(int index)
    {
        if (index >= 0 && index < Items.Count)
        {
            Selected = Items[index];
        }
    }

    internal RungScope Scope() => _scope();

    internal void Changed(RungEditorViewModel? rung)
    {
        _owner.Changed();
        foreach (RungEditorViewModel r in Items)
        {
            if (!ReferenceEquals(r, rung))
            {
                // A rename elsewhere - a tag added, a UDT member renamed - can fix or break any rung.
                r.Recheck();
            }
        }
    }

    private void Insert(int index, RungDraft rung)
    {
        index = Math.Clamp(index, 0, Items.Count);
        _rungs.Insert(index, rung);
        var editor = new RungEditorViewModel(this, rung);
        Items.Insert(index, editor);
        Renumber();
        Selected = editor;
        Changed(editor);
    }

    private void Remove(RungEditorViewModel? rung)
    {
        if (rung is null)
        {
            return;
        }

        int index = Items.IndexOf(rung);
        _rungs.Remove(rung.Draft);
        Items.Remove(rung);
        Renumber();
        Selected = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];
        Changed(null);
    }

    private void Move(RungEditorViewModel? rung, int by)
    {
        if (rung is null)
        {
            return;
        }

        int from = Items.IndexOf(rung);
        int to = from + by;
        if (to < 0 || to >= Items.Count)
        {
            return;
        }

        Items.Move(from, to);
        _rungs.RemoveAt(from);
        _rungs.Insert(to, rung.Draft);
        Renumber();
        Selected = rung;
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        Changed(null);
    }

    private void InsertSnippet(string? snippet)
    {
        if (snippet is null)
        {
            return;
        }

        RungEditorViewModel target = _selected ?? Items.LastOrDefault() ?? Add();
        target.Text = SnippetInsert(target.Text, snippet);
    }

    private RungEditorViewModel Add()
    {
        Insert(Items.Count, new RungDraft(string.Empty));
        return Items[^1];
    }

    /// <summary>
    /// Where a snippet goes: a condition before the rung's trailing outputs, an output at the end.
    /// A placeholder rung - XIC(?)OTE(?) as created - is replaced rather than added to.
    /// </summary>
    internal static string SnippetInsert(string text, string snippet)
    {
        string body = (text ?? string.Empty).Trim().TrimEnd(';');
        if (body.Length == 0 || body == "XIC(?)OTE(?)")
        {
            return snippet + ";";
        }

        string mnemonic = snippet.TrimStart('[').Split('(')[0];
        bool condition = InstructionSignatures.Find(mnemonic)?.IsCondition ?? false;
        if (!condition)
        {
            return body + snippet + ";";
        }

        LadderRung rung = LadderParser.Parse(body);
        if (!rung.IsValid)
        {
            return body + snippet + ";";
        }

        // Insert before the first top-level output.
        int at = body.Length;
        foreach (LadderNode node in rung.Root.Items)
        {
            if (node is LadderInstruction li && InstructionSignatures.Find(li.Instruction.Mnemonic) is { IsCondition: false })
            {
                at = li.Instruction.Position;
                break;
            }
        }

        return body[..at] + snippet + body[at..] + ";";
    }

    private void Renumber()
    {
        for (int i = 0; i < Items.Count; i++)
        {
            Items[i].Number = i;
        }

        OnPropertyChanged(nameof(Summary));
    }
}

/// <summary>A quick-insert button: what it shows, what it inserts, what it is.</summary>
public sealed record SnippetViewModel(string Label, string Text, string ToolTip);

/// <summary>One rung being edited: its text, comment, number and the problems with it right now.</summary>
public sealed class RungEditorViewModel : ObservableObject
{
    private readonly RungListViewModel _list;
    private int _number;
    private IReadOnlyList<string> _problems = [];
    private string _level = ViewModels.Level.None;

    public RungEditorViewModel(RungListViewModel list, RungDraft draft)
    {
        _list = list;
        Draft = draft;
        Recheck();
    }

    public RungDraft Draft { get; }

    public int Number
    {
        get => _number;
        set => SetProperty(ref _number, value);
    }

    public string Text
    {
        get => Draft.Text;
        set
        {
            if (!string.Equals(Draft.Text, value, StringComparison.Ordinal))
            {
                Draft.Text = value ?? string.Empty;
                OnPropertyChanged();
                Recheck();
                _list.Changed(this);
            }
        }
    }

    public string? Comment
    {
        get => Draft.Comment;
        set
        {
            if (!string.Equals(Draft.Comment, value, StringComparison.Ordinal))
            {
                Draft.Comment = string.IsNullOrWhiteSpace(value) ? null : value;
                OnPropertyChanged();
                _list.Changed(this);
            }
        }
    }

    public IReadOnlyList<string> Problems
    {
        get => _problems;
        private set
        {
            if (SetProperty(ref _problems, value))
            {
                OnPropertyChanged(nameof(HasProblems));
            }
        }
    }

    public bool HasProblems => _problems.Count > 0;

    public string Level
    {
        get => _level;
        private set => SetProperty(ref _level, value);
    }

    public Func<string, InstructionShape?> Shapes => _list.Shapes;

    internal void Recheck()
    {
        IReadOnlyList<DraftIssue> issues = DraftChecker.CheckRung(Draft.Text, _list.Scope());
        Problems = issues.Select(i => (i.IsError ? "Error: " : "Warning: ") + i.Message).ToList();
        Level = issues.Any(i => i.IsError) ? ViewModels.Level.Error : issues.Count > 0 ? ViewModels.Level.Warning : ViewModels.Level.None;
        OnPropertyChanged(nameof(Shapes));
    }
}
