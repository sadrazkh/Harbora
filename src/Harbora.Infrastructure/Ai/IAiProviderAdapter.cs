using Harbora.Domain.Ai;

namespace Harbora.Infrastructure.Ai;

/// <summary>What was sent upstream and what came back, without the bodies.</summary>
/// <param name="StatusCode">Null when the request never reached the provider.</param>
/// <param name="InputTokens">As reported by the provider, not estimated here.</param>
/// <param name="CachedInputTokens">Reported separately by providers that cache.</param>
public sealed record AiUpstreamResult(
    bool Ok,
    int? StatusCode,
    string? Body,
    long InputTokens,
    long OutputTokens,
    long CachedInputTokens,
    string? RetryAfterHeader,
    Exception? Exception);

/// <summary>One chunk of a streamed answer.</summary>
/// <param name="Data">The raw SSE data line, passed through unchanged.</param>
/// <param name="IsFinal">True for the terminating chunk.</param>
public sealed record AiStreamChunk(string Data, bool IsFinal);

/// <summary>
/// Talks to one kind of upstream.
///
/// An interface rather than direct calls to OpenRouter, because a platform whose gateway is written
/// against one vendor stops existing the day that vendor has an outage or changes its terms. The
/// adapter converts to and from Harbora's own shapes; nothing above it knows which vendor answered.
/// </summary>
public interface IAiProviderAdapter
{
    /// <summary>Which provider type this adapter handles.</summary>
    AiProviderType Handles { get; }

    /// <summary>
    /// Sends a non-streaming request. Never throws for an upstream failure — the failure is data,
    /// because the router has to decide what to do about it rather than unwind a stack.
    /// </summary>
    Task<AiUpstreamResult> SendAsync(
        AiProvider provider, string token, AiModel model, string requestJson,
        string endpoint, CancellationToken ct);

    /// <summary>
    /// Streams a response, yielding chunks as they arrive.
    ///
    /// Usage is reported at the end by every provider worth using; a caller that stops reading early
    /// must still record what was consumed, which is why the final chunk is marked rather than the
    /// stream simply ending.
    /// </summary>
    IAsyncEnumerable<AiStreamChunk> StreamAsync(
        AiProvider provider, string token, AiModel model, string requestJson,
        string endpoint, CancellationToken ct);
}
