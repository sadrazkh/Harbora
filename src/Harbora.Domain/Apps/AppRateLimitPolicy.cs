namespace Harbora.Domain.Apps;

/// <summary>
/// Bounds and a recommended starting point for an app's own rate limit (C3, 2026-08-27 what's-left
/// plan) — the same shape <see cref="Harbora.Domain.Servers.ServerCapacityPolicy"/> already uses for a
/// server's commitment ratios: constants an admin form shows beside the field so a choice has
/// somewhere to start, never applied on the customer's behalf.
///
/// <para>
/// Traefik's own <c>rateLimit</c> middleware takes three numbers — average, burst and period. Period
/// is deliberately not one of them here: a customer reasoning about "how many requests" does not
/// benefit from also choosing a unit of time to measure them in, and a wrong pairing of the two
/// (a large average over a long period reads as generous and behaves as a trickle) is exactly the kind
/// of mistake a knob with no explanation invites. The period is fixed at
/// <see cref="PeriodSeconds"/> — one minute — and <see cref="Average"/> is phrased to match: "requests
/// allowed per minute". Burst stays a second, separate number, because it answers a different
/// question ("how many of those may arrive at once") that average alone cannot.
/// </para>
/// </summary>
public static class AppRateLimitPolicy
{
    /// <summary>The window every <see cref="Average"/> is counted over. Fixed, not configurable — see
    /// the type's own remarks for why a customer is never asked to choose it.</summary>
    public const int PeriodSeconds = 60;

    public const int MinAverage = 1;

    /// <summary>Above this, the middleware would not meaningfully limit anything a single node could
    /// serve — the same reasoning <see cref="Harbora.Domain.Servers.ServerCapacityPolicy"/> gives its
    /// own ceilings: a bound that cannot bind is worse than none, because it reads as protection.</summary>
    public const int MaxAverage = 1_000_000;

    public const int MinBurst = 1;
    public const int MaxBurst = 1_000_000;

    /// <summary>
    /// Recommended starting point: 300 requests a minute (five a second) sustained. Generous enough
    /// that no ordinary browser session or well-behaved integration ever notices it, tight enough to
    /// blunt a flood — the two failure modes a customer cannot see coming until one of them happens.
    /// </summary>
    public const int RecommendedAverage = 300;

    /// <summary>
    /// Recommended burst: half the per-minute rate. A real visitor's page load fires a handful of
    /// requests at once; a scraper or a retry storm does not stop at half a minute's allowance before
    /// settling into the steady rate. Half, rather than equal to the average, still lets a legitimate
    /// burst through while keeping the instantaneous ceiling below "a whole minute's traffic in one
    /// second".
    /// </summary>
    public const int RecommendedBurst = RecommendedAverage / 2;

    public static bool IsValidAverage(int average) => average is >= MinAverage and <= MaxAverage;

    public static bool IsValidBurst(int burst) => burst is >= MinBurst and <= MaxBurst;
}
