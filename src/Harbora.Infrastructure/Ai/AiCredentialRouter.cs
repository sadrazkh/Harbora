using Harbora.Domain.Ai;

namespace Harbora.Infrastructure.Ai;

/// <summary>How many requests a credential is currently carrying.</summary>
public sealed record CredentialLoad(Guid CredentialId, int InFlight);

/// <summary>Why nothing could be routed to, when nothing could.</summary>
public sealed record RoutingFailure(string Reason);

/// <summary>
/// Picks which provider token carries a request.
///
/// The failure this exists to prevent is sending traffic to a credential that cannot serve it: one
/// that is disabled, rate-limited, out of budget, or failing repeatedly. Each of those looks like a
/// platform outage to the customer while the other credentials sit idle.
///
/// Health-aware weighted least-load. Priority first because an operator says which token they would
/// rather use; then weight, so a token with more headroom takes proportionally more; then actual
/// in-flight load, so a burst does not all land on the same one before any of it completes.
/// </summary>
public static class AiCredentialRouter
{
    /// <summary>
    /// Consecutive failures before a credential is taken out of rotation. Low enough to react
    /// within one customer's retry, high enough that a single blip does not remove a good token.
    /// </summary>
    public const int FailuresBeforeOpen = 5;

    /// <summary>How long an opened circuit stays open before one request is allowed through.</summary>
    public static readonly TimeSpan CircuitCooldown = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Whether this credential can take work right now.
    ///
    /// Every clause is a way a credential can be present and useless. Treating any of them as
    /// healthy sends real customer traffic into a guaranteed failure.
    /// </summary>
    public static bool IsAvailable(AiProviderCredential credential, DateTimeOffset now, decimal? providerBudget)
    {
        if (!credential.IsEnabled) return false;

        // The provider told us to slow down. Sending anyway earns a longer penalty.
        if (credential.RateLimitedUntil is { } until && now < until) return false;

        // Circuit open: too many consecutive failures, and not yet cool. One request is allowed
        // through after the cooldown so a recovered credential can prove itself.
        if (credential.ConsecutiveFailures >= FailuresBeforeOpen)
        {
            var lastFailure = credential.LastFailureAt ?? DateTimeOffset.MinValue;
            if (now - lastFailure < CircuitCooldown) return false;
        }

        // Out of money. Spending past a budget an operator set is worse than refusing the request.
        if (providerBudget is { } budget && budget > 0 && credential.MonthToDateSpend >= budget) return false;

        return true;
    }

    /// <summary>
    /// Chooses a credential, or explains why none could be chosen.
    ///
    /// <paramref name="loads"/> carries current in-flight counts. Missing entries count as zero:
    /// a credential nobody is using is the emptiest one there is.
    /// </summary>
    public static (AiProviderCredential? Chosen, RoutingFailure? Failure) Choose(
        AiProvider provider,
        IEnumerable<AiProviderCredential> credentials,
        IReadOnlyDictionary<Guid, int> loads,
        DateTimeOffset now,
        IReadOnlySet<Guid>? exclude = null)
    {
        if (!provider.IsEnabled)
            return (null, new RoutingFailure("The provider is disabled."));

        var candidates = credentials
            .Where(c => exclude is null || !exclude.Contains(c.Id))
            .Where(c => IsAvailable(c, now, provider.MonthlyBudget))
            .ToList();

        if (candidates.Count == 0)
            return (null, new RoutingFailure("No provider credential is currently available."));

        // Weight zero means "only if nothing else is left", so it is compared last rather than
        // dividing by zero or silently never being chosen.
        var chosen = candidates
            .OrderBy(c => c.Priority)
            .ThenByDescending(c => c.Weight)
            .ThenBy(c => Load(loads, c.Id))
            .ThenBy(c => c.Id)
            .First();

        // Among equal priority, spread by weighted load rather than always taking the first: two
        // tokens of equal priority should share a burst, not queue behind one.
        var samePriority = candidates.Where(c => c.Priority == chosen.Priority).ToList();
        if (samePriority.Count > 1)
        {
            chosen = samePriority
                .OrderBy(c => WeightedLoad(loads, c))
                .ThenByDescending(c => c.Weight)
                .ThenBy(c => c.Id)
                .First();
        }

        return (chosen, null);
    }

    /// <summary>
    /// Load divided by weight, so a token weighted 3 carries three times as much before it is
    /// considered as busy as one weighted 1.
    /// </summary>
    private static double WeightedLoad(IReadOnlyDictionary<Guid, int> loads, AiProviderCredential credential)
    {
        var load = Load(loads, credential.Id);

        // Weight zero is the "last resort" marker; treat it as infinitely loaded so it is only
        // reached when nothing else is available, rather than dividing by zero.
        return credential.Weight <= 0 ? double.MaxValue : load / (double)credential.Weight;
    }

    private static int Load(IReadOnlyDictionary<Guid, int> loads, Guid id) =>
        loads.TryGetValue(id, out var value) ? value : 0;

    /// <summary>Records a success: clears the failure streak and stamps the time.</summary>
    public static void NoteSuccess(AiProviderCredential credential, DateTimeOffset now)
    {
        credential.ConsecutiveFailures = 0;
        credential.LastFailureReason = null;
        credential.LastSuccessAt = now;
    }

    /// <summary>
    /// Records a failure. A rate-limit answer also parks the credential, because retrying into a
    /// rate limit is how a short penalty becomes a long one.
    /// </summary>
    public static void NoteFailure(
        AiProviderCredential credential, DateTimeOffset now, string reason, TimeSpan? retryAfter = null)
    {
        credential.ConsecutiveFailures++;
        credential.LastFailureAt = now;
        credential.LastFailureReason = reason;

        if (retryAfter is { } wait) credential.RateLimitedUntil = now + wait;
    }
}
