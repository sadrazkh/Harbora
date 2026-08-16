namespace Harbora.Infrastructure.Monitoring;

/// <summary>
/// Turns Docker's own ever-climbing restart counter into the one thing worth writing to a time
/// series: how many restarts are attributable to this tick.
///
/// <para>
/// <b>The rule this class exists to state:</b> a restart count is a counter, not a gauge, and
/// <see cref="MetricRollups.IsCumulative"/> was written for exactly this shape of problem — but
/// widening it to cover restarts would only buy the same deal <c>net.rx</c>/<c>net.tx</c> already
/// have: excluded from every rollup, so the raw counter (and any history built on it) dies with the
/// raw samples at <see cref="MetricRollups.RawRetention"/>. A restart-rate rule and a 30-day uptime
/// question both need to outlive that.
/// </para>
/// <para>
/// So the counter itself never becomes a sample. The collector keeps the last count it saw for each
/// container (<see cref="Harbora.Domain.Monitoring.ContainerLifecycleCursor"/>) and this function
/// turns "then" and "now" into a delta before anything is written — always zero or positive, so a
/// container replaced by a redeploy (whose counter starts over at zero) reads as "nothing new this
/// tick" rather than as a fabricated negative dip. What lands in the metrics table is shaped exactly
/// like every other gauge the rollup pipeline already knows how to average and sum, and the standard
/// hourly/daily rollup — no special case anywhere in <c>MetricRollups</c> — recombines those deltas
/// correctly for as long as the daily retention allows, because <c>Average × SampleCount</c> recovers
/// the true total for the period. The one thing that total must never be read as is the <c>Average</c>
/// alone: that number is restarts-per-tick, and displaying it directly is the exact "plausible on a
/// chart but means nothing" mistake this whole design exists to avoid — see
/// <see cref="LifecycleHistory.RestartCountAsync"/>, the one place that recombination happens.
/// </para>
/// </summary>
public static class RestartDelta
{
    /// <summary>
    /// Restarts attributable to this tick — never negative. A current count at or below the previous
    /// one is read as a container replacement (the counter started over), not as restarts undoing
    /// themselves, so it contributes zero rather than a negative delta that would corrupt every sum
    /// this series is later rolled up into.
    /// </summary>
    public static long Between(int previousCount, int currentCount) =>
        Math.Max(0, currentCount - previousCount);
}
