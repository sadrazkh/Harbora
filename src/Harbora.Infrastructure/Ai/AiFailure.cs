namespace Harbora.Infrastructure.Ai;

/// <summary>What kind of failure came back, which decides what to do about it.</summary>
public enum AiFailureKind
{
    /// <summary>The provider is overloaded or we are sending too fast. Park this credential.</summary>
    RateLimited = 0,

    /// <summary>The credential is bad — expired, revoked, out of credit. Do not retry it.</summary>
    CredentialRejected = 1,

    /// <summary>The provider broke. Another credential may work.</summary>
    ProviderError = 2,

    /// <summary>We could not reach them at all.</summary>
    Network = 3,

    /// <summary>The request itself is wrong. Retrying it anywhere produces the same answer.</summary>
    BadRequest = 4,

    /// <summary>Something we do not recognise.</summary>
    Unknown = 5
}

/// <summary>What to do about a failure.</summary>
/// <param name="Kind">How it was classified.</param>
/// <param name="RetryElsewhere">Whether another credential is worth trying.</param>
/// <param name="ParkCredential">Whether this credential should be taken out of rotation.</param>
/// <param name="RetryAfter">How long the provider asked us to wait, when they said.</param>
public sealed record AiFailureVerdict(
    AiFailureKind Kind, bool RetryElsewhere, bool ParkCredential, TimeSpan? RetryAfter);

/// <summary>
/// Reads a provider failure and decides what to do.
///
/// The distinction that matters is between a failure of the credential and a failure of the request.
/// Retrying a bad request across every credential in turn burns all of them and returns the same
/// error more slowly — and, for anything that is not idempotent, may do the work several times.
/// </summary>
public static class AiFailureClassifier
{
    public static AiFailureVerdict Classify(int? statusCode, string? retryAfterHeader = null, Exception? exception = null)
    {
        var retryAfter = ParseRetryAfter(retryAfterHeader);

        if (exception is not null && statusCode is null)
            return new AiFailureVerdict(AiFailureKind.Network, RetryElsewhere: true, ParkCredential: false, retryAfter);

        return statusCode switch
        {
            429 => new AiFailureVerdict(AiFailureKind.RateLimited, true, true,
                // Park for a sensible minimum even when they do not say: retrying immediately into
                // a rate limit is how a short penalty becomes a long one.
                retryAfter ?? TimeSpan.FromSeconds(20)),

            401 or 403 => new AiFailureVerdict(AiFailureKind.CredentialRejected, true, true, retryAfter),

            // Out of credit reads as a payment problem with this account, not with the request.
            402 => new AiFailureVerdict(AiFailureKind.CredentialRejected, true, true, retryAfter),

            // The request is wrong. Another credential returns the same answer.
            >= 400 and < 500 => new AiFailureVerdict(AiFailureKind.BadRequest, false, false, retryAfter),

            >= 500 => new AiFailureVerdict(AiFailureKind.ProviderError, true, false, retryAfter),

            _ => new AiFailureVerdict(AiFailureKind.Unknown, false, false, retryAfter)
        };
    }

    /// <summary>
    /// Whether this request may be sent again at all.
    ///
    /// A streamed response that already reached the customer cannot be replayed — they have seen
    /// part of an answer, and a second attempt would either duplicate it or contradict it. Anything
    /// already charged for is in the same position.
    /// </summary>
    public static bool IsSafeToRetry(bool responseStarted, bool usageRecorded) =>
        !responseStarted && !usageRecorded;

    /// <summary>
    /// How long to wait before attempt <paramref name="attempt"/>, counting from one.
    ///
    /// Exponential with a ceiling. Without the ceiling a third retry waits long enough that the
    /// customer's own client has already given up, so the wait costs a connection and buys nothing.
    /// </summary>
    public static TimeSpan Backoff(int attempt)
    {
        if (attempt <= 1) return TimeSpan.Zero;

        var seconds = Math.Pow(2, attempt - 1);
        return TimeSpan.FromSeconds(Math.Min(seconds, 8));
    }

    private static TimeSpan? ParseRetryAfter(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;

        // Seconds, the common form.
        if (int.TryParse(header.Trim(), out var seconds) && seconds >= 0)
            return TimeSpan.FromSeconds(Math.Min(seconds, 300));

        // An HTTP date, the other legal form.
        if (DateTimeOffset.TryParse(header, out var when))
        {
            var wait = when - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? TimeSpan.FromSeconds(Math.Min(wait.TotalSeconds, 300)) : TimeSpan.Zero;
        }

        return null;
    }
}
