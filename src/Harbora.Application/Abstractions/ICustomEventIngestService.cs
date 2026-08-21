namespace Harbora.Application.Abstractions;

/// <summary>
/// Takes a custom event a customer's own function-app code raised and hands it to
/// <see cref="Harbora.Domain.Functions.FunctionEvent"/>'s existing plumbing (F3, 2026-08-21
/// functions-and-services plan, "Custom events from customer apps"). The counterpart of
/// <see cref="IGitWebhookProcessor"/>: same anonymous-door shape (an id in the URL pins the scope,
/// a shared secret proves the caller), reused rather than reinvented because the trap is identical —
/// this call has no session, so the tenant filter would hide the very app whose own secret is about
/// to prove it.
/// </summary>
public interface ICustomEventIngestService
{
    Task<CustomEventIngestResult> IngestAsync(
        Guid appId, string? providedSecret, CustomEventIngestRequest request, CancellationToken ct);
}

/// <summary>What the panel extracted from the POST — the raw key, as the caller typed it, before the
/// namespace is forced.</summary>
public sealed record CustomEventIngestRequest(
    string? Key, string? Subject, IReadOnlyDictionary<string, string?>? Data);

public enum CustomEventIngestOutcome
{
    /// <summary>Queued for whichever functions in this app's own workspace subscribed to it.</summary>
    Accepted,

    /// <summary>No app answers this id, or its secret does not match what was presented — deliberately
    /// not distinguished in the HTTP response, the same choice <c>WebhooksController</c> already makes
    /// for an unknown repository vs. a bad signature.</summary>
    Unauthorized,

    /// <summary>The posted key had nothing usable in it once normalised (empty, or only characters a
    /// key cannot contain).</summary>
    InvalidKey
}

/// <param name="Key">The key actually recorded — always under <c>custom.</c> — or null when nothing
/// was accepted.</param>
public sealed record CustomEventIngestResult(CustomEventIngestOutcome Outcome, string? Key = null);
