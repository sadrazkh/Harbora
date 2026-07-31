namespace Harbora.Infrastructure.Assistant;

/// <summary>Exactly what would be sent, assembled so it can be shown before it is.</summary>
/// <param name="SystemPrompt">Instructions to the model. Carries no data about this installation.</param>
/// <param name="UserPrompt">The question and the redacted log — the only part that leaves.</param>
/// <param name="Removed">How many secrets were taken out of the log on the way.</param>
/// <param name="Truncated">Whether the log was too long to send whole.</param>
public sealed record AssistantAsk(string SystemPrompt, string UserPrompt, int Removed, bool Truncated);

/// <summary>
/// Turns a failed deployment into a question.
///
/// Two decisions live here. The first is how much log to send: all of it is both expensive and
/// useless, because the cause of a failure is at the end. The tail is kept, never the head.
///
/// The second is that the assembled text is returned rather than sent. Nothing leaves this server
/// until somebody has read the exact bytes that would leave it — which is only meaningful if the
/// thing they are shown *is* the thing that is sent, so there is one function and it produces both.
/// </summary>
public static class AssistantRequest
{
    /// <summary>
    /// How much log travels. Roughly a few thousand tokens: enough for a stack trace and the build
    /// steps around it, small enough that nobody is surprised by a bill.
    /// </summary>
    public const int MaxLogCharacters = 12_000;

    private const string Instructions =
        "You are helping someone debug a failed deployment on a self-hosted PaaS. " +
        "Explain what went wrong and what to change, briefly and concretely. " +
        "The log has been redacted: text marked [redacted] was removed before you saw it — " +
        "never ask for it and never guess what it was. " +
        "If the log does not say why it failed, say so plainly rather than inventing a cause.";

    /// <summary>
    /// Builds the question. <paramref name="knownSecrets"/> is every secret value the service holds,
    /// decrypted, so they can be recognised in the log and removed.
    /// </summary>
    public static AssistantAsk ForFailedDeployment(
        string? log,
        string? errorMessage,
        string? serviceKind,
        IEnumerable<string>? knownSecrets = null)
    {
        // Redact first, then trim. The other order would measure the length of text that is about to
        // change, and — worse — a secret straddling the cut would be half-masked.
        var redactedLog = AssistantRedaction.Redact(log, knownSecrets);
        var redactedError = AssistantRedaction.Redact(errorMessage, knownSecrets);

        var (tail, truncated) = Tail(redactedLog.Text, MaxLogCharacters);

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(serviceKind))
            parts.Add($"Service type: {serviceKind}");
        if (!string.IsNullOrWhiteSpace(redactedError.Text))
            parts.Add($"Harbora reported: {redactedError.Text}");

        parts.Add(truncated
            ? $"The last {tail.Length} characters of the deployment log:"
            : "The deployment log:");
        parts.Add(tail.Length == 0 ? "(the deployment produced no log)" : tail);

        return new AssistantAsk(
            Instructions,
            string.Join("\n\n", parts),
            redactedLog.Removed + redactedError.Removed,
            truncated);
    }

    /// <summary>
    /// The end of the log, cut at a line boundary so the model is not handed half a line as though
    /// it were whole.
    /// </summary>
    private static (string Text, bool Truncated) Tail(string text, int limit)
    {
        if (text.Length <= limit) return (text, false);

        var cut = text[^limit..];
        var firstBreak = cut.IndexOf('\n');

        // If the first line break is the very last character there is nothing whole to keep, so the
        // ragged cut is better than an empty one.
        if (firstBreak >= 0 && firstBreak < cut.Length - 1)
            cut = cut[(firstBreak + 1)..];

        return (cut, true);
    }
}
