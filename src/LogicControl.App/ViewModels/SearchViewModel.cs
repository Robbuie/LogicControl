using System.Globalization;
using LogicControl.App.Composition;
using LogicControl.Core.Analysis;

namespace LogicControl.App.ViewModels;

/// <summary>
/// The Search tab: text or a tag name looked for in every rung, rung comment and line of
/// structured text of the open project. A result opens its rung on the Logic tab.
/// </summary>
public sealed class SearchViewModel : ObservableObject
{
    private readonly Func<ProjectAnalysis?> _analysis;
    private string _query = string.Empty;
    private bool _wholeTag;
    private IReadOnlyList<SearchHit> _results = [];
    private string _summary = "Type text or a tag name and press Enter. Searches every rung, rung comment and line of structured text.";

    public SearchViewModel(Func<ProjectAnalysis?> analysis)
    {
        _analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
        SearchCommand = new RelayCommand(Search, () => _analysis() is not null && _query.Trim().Length > 0);
    }

    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value ?? string.Empty))
            {
                SearchCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>Match a tag by name - Motor finds Motor.Run, not Motor2 - rather than any text.</summary>
    public bool WholeTag
    {
        get => _wholeTag;
        set
        {
            if (SetProperty(ref _wholeTag, value) && _results.Count > 0)
            {
                Search();
            }
        }
    }

    public IReadOnlyList<SearchHit> Results
    {
        get => _results;
        private set => SetProperty(ref _results, value);
    }

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public RelayCommand SearchCommand { get; }

    public void Search()
    {
        if (_analysis() is not { } analysis || _query.Trim().Length == 0)
        {
            return;
        }

        Results = LogicSearch.Find(analysis.Project, _query, _wholeTag);
        int routines = Results.Select(r => r.Site.Routine).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        Summary = Results.Count == 0
            ? $"Nothing found for \"{_query.Trim()}\"{(_wholeTag ? " as a tag" : string.Empty)}."
            : Results.Count >= LogicSearch.Limit
                ? $"The first {Count(Results.Count)} places, in {Count(routines)} routines. Narrow the search to see the rest."
                : $"{Count(Results.Count)} place{(Results.Count == 1 ? string.Empty : "s")} in {Count(routines)} routine{(routines == 1 ? string.Empty : "s")}. Double-click one to open it.";
    }

    /// <summary>A different project is open: what was found was found in the last one.</summary>
    public void Clear()
    {
        Results = [];
        Summary = "Type text or a tag name and press Enter. Searches every rung, rung comment and line of structured text.";
        SearchCommand.NotifyCanExecuteChanged();
    }

    private static string Count(int n) => n.ToString("N0", CultureInfo.CurrentCulture);
}
