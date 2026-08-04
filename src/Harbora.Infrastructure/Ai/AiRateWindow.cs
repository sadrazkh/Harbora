using Harbora.Domain.Ai;

namespace Harbora.Infrastructure.Ai;

/// <summary>One request that has already been counted, and what it cost in tokens.</summary>
/// <param name="At">When it started.</param>
/// <param name="Tokens">
/// Input plus output, filled in when the response finished. Zero for a request still in flight —
/// tokens are not knowable until the provider has answered.
/// </param>
public readonly record struct RateEvent(DateTimeOffset At, long Tokens);

/// <summary>Whether a request may start, and how long to wait when it may not.</summary>
public sealed record RateDecision(AiRefusal? Refusal, int RetryAfterSeconds)
{
    public bool Allowed => Refusal is null;
    public static readonly RateDecision Ok = new(null, 0);
}

/// <summary>
/// The per-plan rate limits, as a pure decision over what has already happened.
///
/// Sliding windows, not fixed ones. A fixed minute lets a caller send the whole minute's allowance
/// at 11:59:59 and the next minute's at 12:00:00 — twice the limit in two seconds, which is exactly
/// the burst a rate limit exists to stop, and it passes every test written against a fixed window.
///
/// Limits are checked longest-window first so the wait reported is the real one. A caller who has
/// exhausted both the day and the minute and is told to retry in 60 seconds will retry in 60 seconds
/// and fail again, and again, which reads as a broken gateway rather than a limit.
/// </summary>
public static class AiRateWindow
{
    public static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan Day = TimeSpan.FromDays(1);

    /// <summary>
    /// Whether one more request may start.
    /// </summary>
    /// <param name="events">Requests counted so far, in any order. Anything older than a day is ignored.</param>
    /// <param name="inFlight">Requests this tenant currently has open, not counting this one.</param>
    public static RateDecision Refuse(
        AiPlan plan, IReadOnlyCollection<RateEvent> events, int inFlight, DateTimeOffset now)
    {
        // A limit of zero blocks rather than opens. An administrator who clears the field breaks
        // their customers, who say so within the hour; one who accidentally removes every limit is
        // told by the provider invoice a month later.
        if (plan.RequestsPerDay <= 0)
            return Blocked("daily_limit", "Your plan allows no requests.");

        if (plan.RequestsPerMinute <= 0)
            return Blocked("rate_limit", "Your plan allows no requests.");

        if (plan.ConcurrentRequests <= 0)
            return Blocked("too_many_concurrent", "Your plan allows no concurrent requests.");

        var dayStart = now - Day;
        var minuteStart = now - Minute;

        var inDay = events.Where(e => e.At > dayStart).ToList();
        if (inDay.Count >= plan.RequestsPerDay)
            return Refusal("daily_limit", $"Your plan allows {plan.RequestsPerDay} requests a day.",
                Wait(inDay.Min(e => e.At) + Day, now));

        var inMinute = inDay.Where(e => e.At > minuteStart).ToList();
        if (inMinute.Count >= plan.RequestsPerMinute)
            return Refusal("rate_limit", $"Your plan allows {plan.RequestsPerMinute} requests a minute.",
                Wait(inMinute.Min(e => e.At) + Minute, now));

        // Tokens are only known once a response has finished, so this counts what has already been
        // spent rather than what this request will spend. One large request can therefore carry the
        // total past the limit; the next one is refused. Reserving an estimate up front would refuse
        // requests that turn out to be small, which is the worse error for a paying customer.
        if (plan.TokensPerMinute > 0)
        {
            var tokens = inMinute.Sum(e => e.Tokens);
            if (tokens >= plan.TokensPerMinute)
                return Refusal("token_rate_limit",
                    $"Your plan allows {plan.TokensPerMinute} tokens a minute.",
                    Wait(inMinute.Min(e => e.At) + Minute, now));
        }

        // Last, and with the shortest wait: this one clears as soon as a request in progress
        // finishes, which may be sooner than any window.
        if (inFlight >= plan.ConcurrentRequests)
            return Refusal("too_many_concurrent",
                $"Your plan allows {plan.ConcurrentRequests} requests at once.", 1);

        return RateDecision.Ok;
    }

    /// <summary>Events still worth keeping. Anything older than the longest window cannot matter.</summary>
    public static IReadOnlyList<RateEvent> Prune(IEnumerable<RateEvent> events, DateTimeOffset now)
    {
        var cutoff = now - Day;
        return events.Where(e => e.At > cutoff).ToList();
    }

    private static RateDecision Refusal(string code, string message, int retryAfter) =>
        new(new AiRefusal(429, code, message), retryAfter);

    private static RateDecision Blocked(string code, string message) =>
        // No retry time, because waiting will not help: the plan itself has to change.
        new(new AiRefusal(429, code, message), 0);

    private static int Wait(DateTimeOffset until, DateTimeOffset now)
    {
        var seconds = (until - now).TotalSeconds;

        // At least one: a Retry-After of zero invites an immediate retry that is certain to fail,
        // and a client honouring it turns one refusal into a spin.
        return seconds <= 1 ? 1 : (int)Math.Ceiling(seconds);
    }
}
