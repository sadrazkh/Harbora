namespace Harbora.Infrastructure.Monitoring;

/// <summary>
/// Turns "how often" into "which key" — a stable label for the window <c>now</c> falls in, so two
/// calls made at different instants inside the same window agree on one bucket without either of them
/// having to remember the other happened.
///
/// <para>
/// Aligned to the Unix epoch rather than to the first call's own timestamp: an epoch-relative bucket
/// is a pure function of <c>now</c> and <paramref name="window"/> alone, so the same instant lands in
/// the same bucket regardless of which process asks, or how many times — the property
/// <c>AlertDedup</c> depends on. The trade this accepts, stated so it is not rediscovered later: a
/// bucket boundary is fixed by the clock, not by the last fire, so two fires can in principle land
/// under two minutes apart if the first happens just before a boundary and the second just after —
/// the same trade <c>MonitoringOptions.DiskAlertIntervalHours</c>'s shipped default already makes at
/// the top of every hour. What this buys back is a mechanism the previous one could not offer:
/// durability. A row keyed by bucket needs nothing remembered between calls; a sliding window needs
/// the last fire remembered somewhere that survives a restart, which was exactly
/// <c>AlertThrottle</c>'s own admitted limit.
/// </para>
/// </summary>
public static class AlertDedupWindow
{
    /// <summary>
    /// The bucket <paramref name="now"/> falls in for a window of <paramref name="window"/>, as a
    /// string ready to go into a dedup key. <paramref name="window"/> must be positive — a caller
    /// whose configured interval is zero or negative means "never throttle", which is a decision to
    /// skip calling this (and the dedup check it feeds) entirely, not a bucket to compute.
    /// </summary>
    public static string Bucket(DateTimeOffset now, TimeSpan window)
    {
        if (window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window),
                "A zero or negative window has no bucket. The caller should skip deduplication " +
                "instead of asking for one — see MetricsCollector.MaybeDiskAlert.");

        var size = (long)window.TotalMilliseconds;
        return (now.ToUnixTimeMilliseconds() / size).ToString();
    }
}
