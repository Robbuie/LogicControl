namespace LogicControl.Core.Assistant;

/// <summary>
/// A chat with Claude that can use LogicControl's tools - the assistant panel's back end. Two
/// implementations: <see cref="ConversationSession"/> talks to the Messages API with an API key;
/// <see cref="ClaudeCodeSession"/> drives the user's own Claude Code, signed in with their Claude
/// plan. The panel does not care which.
/// </summary>
public interface IConversation
{
    /// <summary>The model this conversation was started with.</summary>
    string Model { get; }

    /// <summary>Tokens used since the chat started.</summary>
    Usage TotalUsage { get; }

    /// <summary>How many messages are in the history - 0 for a fresh chat.</summary>
    int MessageCount { get; }

    /// <summary>
    /// Sends <paramref name="userText"/>, with <paramref name="context"/> (what the user is looking
    /// at) in front of it, and runs the turn to the end, streaming text and tool activity to
    /// <paramref name="sink"/>. Throws <see cref="ClaudeApiException"/> when Claude cannot be
    /// reached or refuses, and <see cref="OperationCanceledException"/> when stopped.
    /// </summary>
    Task RunTurnAsync(string userText, string? context, IAssistantSink sink, CancellationToken cancellationToken = default);

    /// <summary>Forgets the history: the next message starts a new chat.</summary>
    void Reset();
}
