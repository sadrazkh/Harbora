namespace Harbora.NodeAgent.Transport;

/// <summary>
/// How long to wait before the next connection attempt.
///
/// <para>
/// Exponential backoff with <em>full</em> jitter — the delay is a uniform draw from
/// <c>[0, computed]</c> rather than the computed value plus a wiggle. A control-plane restart
/// disconnects every node at once; with a deterministic backoff they all return in the same
/// instant and knock it over again, and the fleet oscillates. Full jitter spreads the herd across
/// the whole window, which is the only variant that actually decorrelates it.
/// </para>
/// </summary>
public sealed class ReconnectPolicy(ReconnectOptions options, Func<double>? random = null)
{
    private readonly Func<double> _random = random ?? Random.Shared.NextDouble;

    /// <summary>Delay before attempt number <paramref name="attempt"/> (1-based).</summary>
    public TimeSpan Delay(int attempt)
    {
        if (attempt <= 1) return TimeSpan.Zero;

        var exponent = attempt - 2;
        var uncapped = options.InitialDelayMs * Math.Pow(options.Multiplier, exponent);

        // Math.Pow overflows to infinity long before an attempt count that could be reached in
        // practice, but a node left disconnected for a week does reach it.
        var capped = double.IsFinite(uncapped)
            ? Math.Min(uncapped, options.MaxDelayMs)
            : options.MaxDelayMs;

        var milliseconds = options.Jitter ? capped * _random() : capped;

        return TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
    }
}
