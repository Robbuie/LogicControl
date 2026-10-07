using LogicControl.Core.Analysis;

namespace LogicControl.App.ViewModels;

/// <summary>
/// The status words the views key their colours on. Strings rather than an enum so a XAML
/// DataTrigger can match them with a plain Value="Error" - and there is one more state than
/// <see cref="FindingSeverity"/> has: a row with nothing to say.
/// </summary>
public static class Level
{
    public const string Error = "Error";
    public const string Warning = "Warning";
    public const string Info = "Info";
    public const string None = "None";

    public static string Of(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Error => Error,
        FindingSeverity.Warning => Warning,
        _ => Info,
    };

    /// <summary>The most serious of a set of findings, or <see cref="None"/>.</summary>
    public static string Worst(IEnumerable<Finding> findings)
    {
        FindingSeverity? worst = null;
        foreach (Finding f in findings)
        {
            if (worst is null || f.Severity < worst)
            {
                worst = f.Severity;
            }
        }

        return worst is { } w ? Of(w) : None;
    }
}
