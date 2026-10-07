using LogicControl.Core.Analysis;

namespace LogicControl.App.ViewModels;

public sealed class FindingRowViewModel(Finding finding)
{
    public Finding Finding { get; } = finding ?? throw new ArgumentNullException(nameof(finding));

    public string Level => ViewModels.Level.Of(Finding.Severity);

    public string Rule => Finding.Rule;

    public string Category => Finding.Category;

    public string Subject => Finding.Subject;

    public string Message => Finding.Message;

    public string Location => Finding.Location ?? string.Empty;

    public string SearchText => $"{Level} {Rule} {Category} {Subject} {Message} {Location}";
}
