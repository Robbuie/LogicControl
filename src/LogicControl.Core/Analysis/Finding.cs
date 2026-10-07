namespace LogicControl.Core.Analysis;

/// <summary>
/// Something worth a person's attention. <see cref="Rule"/> is a stable id (LC-HW-001) so a
/// finding can be looked up, discussed and, later, suppressed by name.
/// </summary>
public sealed record Finding(
    FindingSeverity Severity,
    string Rule,
    string Category,
    string Subject,
    string Message,
    string? Location = null);

/// <summary>Ordered most serious first, so a sort on it puts errors at the top.</summary>
public enum FindingSeverity
{
    Error,
    Warning,
    Info,
}
