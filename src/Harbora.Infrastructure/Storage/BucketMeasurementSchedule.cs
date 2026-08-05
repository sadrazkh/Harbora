namespace Harbora.Infrastructure.Storage;

/// <summary>A bucket as the schedule sees it: an identity and when it was last measured.</summary>
public readonly record struct MeasurableBucket(Guid Id, DateTimeOffset? MeasuredAt);

/// <summary>
/// Which buckets to measure on this tick.
///
/// Measuring runs a container against the storage server, so the sweep cannot simply do all of them
/// every time: an installation with two hundred buckets would spend its life starting containers,
/// and the figures are not worth that. What it must not do instead is starve any of them — a bucket
/// that never reaches the front of the queue is a bucket whose usage is permanently unknown, which
/// is the state this whole thing exists to leave behind.
///
/// Oldest first, never-measured before everything, and a cap per tick. Those three together mean
/// every bucket is reached: the ones skipped this tick are the newest measurements, so they are the
/// oldest next time.
/// </summary>
public static class BucketMeasurementSchedule
{
    /// <summary>How stale a figure has to be before it is worth another container.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(6);

    /// <summary>
    /// How many to measure in one pass. Small on purpose: the sweep runs forever, so it can afford
    /// to take several passes, and a burst of containers is felt by everything else on the host.
    /// </summary>
    public const int DefaultBatch = 5;

    /// <summary>
    /// The buckets due for measurement, most overdue first.
    /// </summary>
    public static IReadOnlyList<Guid> Due(
        IEnumerable<MeasurableBucket> buckets,
        DateTimeOffset now,
        TimeSpan? interval = null,
        int batch = DefaultBatch)
    {
        var window = interval ?? DefaultInterval;

        return buckets
            // Never measured is not "measured a moment ago". Ordering by the raw timestamp would
            // work by accident today — nulls sort first — and stop working the moment somebody
            // substitutes a default for the null, which reads like a tidy-up.
            .Where(b => b.MeasuredAt is not { } at || now - at >= window)
            .OrderBy(b => b.MeasuredAt.HasValue)
            .ThenBy(b => b.MeasuredAt ?? DateTimeOffset.MinValue)
            // Take already yields nothing for zero or a negative count, so an explicit guard for
            // those went in here and came out again: it changed no outcome, and a redundant guard
            // reads as the one doing the work until somebody weakens the one that is. The behaviour
            // is pinned by a test either way.
            .Take(batch)
            .Select(b => b.Id)
            .ToList();
    }
}
