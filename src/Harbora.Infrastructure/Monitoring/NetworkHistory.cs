using Harbora.Data;
using Harbora.Domain.Monitoring;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Monitoring;

/// <summary>A rate at a moment, or a gap where one could not be worked out.</summary>
public sealed record ThroughputPoint(DateTimeOffset At, double? BytesPerSecond);

/// <summary>
/// Network throughput over time, from the counters the collector stores.
///
/// The counters are cumulative, so every point here is the difference between two of them — and the
/// points where that difference cannot be trusted are kept, as gaps, rather than dropped. A chart
/// that silently closes over a container restart draws a smooth line through an outage.
/// </summary>
public sealed class NetworkHistory(HarboraDbContext db)
{
    /// <summary>
    /// Throughput for a server, or for one container when <paramref name="resourceRef"/> is given.
    /// Returns an empty list when nothing has been collected — which the honesty gate renders as
    /// "not collected yet" rather than as zero.
    /// </summary>
    public async Task<IReadOnlyList<ThroughputPoint>> ForAsync(
        Guid serverId, string metric, DateTimeOffset since, string? resourceRef, CancellationToken ct)
    {
        var samples = await db.Set<MonitoringMetric>().IgnoreQueryFilters()
            .Where(m => m.ServerId == serverId && m.Name == metric && m.Timestamp >= since
                        && m.ResourceRef == resourceRef)
            .OrderBy(m => m.Timestamp)
            .Select(m => new { m.Timestamp, m.Value })
            .ToListAsync(ct);

        var points = new List<ThroughputPoint>();
        for (var i = 1; i < samples.Count; i++)
        {
            var previous = samples[i - 1];
            var current = samples[i];

            points.Add(new ThroughputPoint(
                current.Timestamp,
                NetworkThroughput.Between(
                    (long)previous.Value, previous.Timestamp, (long)current.Value, current.Timestamp)));
        }

        return points;
    }

    /// <summary>
    /// The most recent rate that could be worked out, or null. Used for the single number on a card,
    /// where the last point being a gap should fall back to the last real reading rather than
    /// reporting nothing — but only within the window, so a stale figure cannot masquerade as now.
    /// </summary>
    public static double? Latest(IReadOnlyList<ThroughputPoint> points) =>
        points.LastOrDefault(p => p.BytesPerSecond is not null)?.BytesPerSecond;
}
