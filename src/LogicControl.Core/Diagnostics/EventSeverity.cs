namespace LogicControl.Core.Diagnostics;

/// <summary>How much attention a diagnostic line needs. Ported from NetControl.</summary>
public enum EventSeverity
{
    /// <summary>Something happened.</summary>
    Info,

    /// <summary>Worth a look, but the operation continued.</summary>
    Warn,

    /// <summary>The operation did not do what was asked.</summary>
    Error,
}
