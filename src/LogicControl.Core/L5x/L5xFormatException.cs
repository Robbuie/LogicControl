namespace LogicControl.Core.L5x;

/// <summary>
/// The file is not an L5X export this reader understands. The message is written for the person
/// who picked the file, not for a developer - it says what was expected and what to do instead.
/// </summary>
public sealed class L5xFormatException : Exception
{
    public L5xFormatException()
    {
    }

    public L5xFormatException(string message)
        : base(message)
    {
    }

    public L5xFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
