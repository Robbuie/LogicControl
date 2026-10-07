namespace LogicControl.Core.Analysis;

/// <summary>
/// Something worth a person's attention. <see cref="Rule"/> is a stable id (LC-HW-001) so a
/// finding can be looked up, discussed and, later, suppressed by name.
///
/// <para><see cref="Location"/> is the text a person reads; <see cref="Sites"/> is the same places
/// as data, so the findings list can open the routine at the rung.</para>
/// </summary>
public sealed record Finding(
    FindingSeverity Severity,
    string Rule,
    string Category,
    string Subject,
    string Message,
    string? Location = null,
    IReadOnlyList<FindingSite>? Sites = null)
{
    /// <summary>The rungs and lines the finding is about, in project order. Empty when it is about hardware or a tag list.</summary>
    public IReadOnlyList<FindingSite> Sites { get; init; } = Sites ?? [];
}

/// <summary>Ordered most serious first, so a sort on it puts errors at the top.</summary>
public enum FindingSeverity
{
    Error,
    Warning,
    Info,
}

/// <summary>
/// One place in the logic a finding points at: a routine (Program/Routine, or Aoi/Logic) and a
/// rung or ST line in it. <see cref="Location"/> is null when the finding is about the routine as
/// a whole - one nothing calls, say.
/// </summary>
public sealed record FindingSite(string Routine, int? Location, bool IsStructuredText = false)
{
    public string Text => Location is not { } at
        ? Routine
        : $"{Routine} {(IsStructuredText ? "Line" : "Rung")} {at.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    public static FindingSite Of(InstructionSite site)
    {
        ArgumentNullException.ThrowIfNull(site);
        return new FindingSite(site.Routine.QualifiedName, site.Location, site.Routine.Language == Model.RoutineLanguage.StructuredText);
    }

    public static FindingSite Of(Logic.TagUse use)
    {
        ArgumentNullException.ThrowIfNull(use);
        return new FindingSite(use.Routine, use.Location, use.IsStructuredText);
    }
}
