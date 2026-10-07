using System.Globalization;
using LogicControl.Core.Analysis;

namespace LogicControl.App.ViewModels;

/// <summary>One communication link as a grid row.</summary>
public sealed class CommRowViewModel(CommLink link)
{
    public CommLink Link { get; } = link ?? throw new ArgumentNullException(nameof(link));

    public string Kind => Link.KindText;

    /// <summary>The enum name - Io, Message... - for the views' colour triggers.</summary>
    public string KindKey => Link.Kind.ToString();

    public string From => Link.From;

    public string To => Link.To;

    public string Address => Link.Address ?? string.Empty;

    public string Rpi => Link.RpiMs is { } r ? r.ToString("0.##", CultureInfo.InvariantCulture) + " ms" : string.Empty;

    public string Detail => Link.Detail ?? string.Empty;

    public string Source => Link.Source ?? string.Empty;

    public string SearchText => $"{Kind} {From} {To} {Address} {Detail} {Source}";
}
