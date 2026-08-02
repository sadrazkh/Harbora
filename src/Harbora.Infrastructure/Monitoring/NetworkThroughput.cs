namespace Harbora.Infrastructure.Monitoring;

/// <summary>
/// Bytes per second, from two of Docker's cumulative counters.
///
/// The counters climb from the moment a container starts, so charting them raw draws a line that
/// only ever rises and says nothing about traffic. The rate is the difference over the interval.
///
/// Every branch that returns null exists because the alternative is a number that looks measured and
/// is not. A container restart resets the counter, and subtracting across that produces either a
/// negative rate or — clamped — a spike that reads as a traffic flood at precisely the moment the
/// service was down. A long gap averages an unknown hour into one flat point. Both are unknown, and
/// unknown is not zero.
/// </summary>
public static class NetworkThroughput
{
    /// <summary>
    /// The longest interval still worth calling a rate. Past this the average says more about the
    /// outage than about the traffic.
    /// </summary>
    public static readonly TimeSpan MaxGap = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The rate between two samples, or null when it cannot honestly be known.
    /// </summary>
    /// <param name="previousBytes">The counter at the earlier sample, or null if there was none.</param>
    /// <param name="currentBytes">The counter now.</param>
    public static double? Between(
        long? previousBytes, DateTimeOffset? previousAt,
        long currentBytes, DateTimeOffset currentAt)
    {
        // Nothing to compare against — the first tick after a container starts.
        if (previousBytes is not { } previous || previousAt is not { } since) return null;

        // The counter went backwards, which only happens when it was reset. What ran in between is
        // genuinely unknowable from here.
        if (currentBytes < previous) return null;

        var elapsed = currentAt - since;

        // Same instant, or out of order: no interval to divide by.
        if (elapsed <= TimeSpan.Zero) return null;

        // Too long to average over.
        if (elapsed > MaxGap) return null;

        return (currentBytes - previous) / elapsed.TotalSeconds;
    }
}
