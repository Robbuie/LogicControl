using LogicControl.App.Composition;
using LogicControl.Core.Analysis;

namespace LogicControl.App.ViewModels;

public sealed class FindingRowViewModel(Finding finding, Action<FindingRowViewModel, bool>? accept = null, bool accepted = false) : ObservableObject
{
    private bool _isAccepted = accepted;

    public Finding Finding { get; } = finding ?? throw new ArgumentNullException(nameof(finding));

    /// <summary>Somebody has looked at it and accepted it: it is kept out of the list unless accepted findings are shown.</summary>
    public bool IsAccepted
    {
        get => _isAccepted;
        set
        {
            if (accept is not null && SetProperty(ref _isAccepted, value))
            {
                accept(this, value);
            }
        }
    }

    /// <summary>Whether this row can be accepted - the open project's findings, not the system view's.</summary>
    public bool CanAccept => accept is not null;

    public string Level => ViewModels.Level.Of(Finding.Severity);

    public string Rule => Finding.Rule;

    public string Category => Finding.Category;

    public string Subject => Finding.Subject;

    public string Message => Finding.Message;

    public string Location => Finding.Location ?? string.Empty;

    /// <summary>The rungs and lines it is about - each a link that opens the routine there.</summary>
    public IReadOnlyList<FindingSite> Sites => Finding.Sites;

    public bool HasSites => Finding.Sites.Count > 0;

    /// <summary>The "Where" column as plain text, for findings with no rung to open.</summary>
    public bool ShowLocationText => !HasSites && Location.Length > 0;

    public string SearchText => $"{Level} {Rule} {Category} {Subject} {Message} {Location} {string.Join(" ", Sites.Select(s => s.Text))}";
}
