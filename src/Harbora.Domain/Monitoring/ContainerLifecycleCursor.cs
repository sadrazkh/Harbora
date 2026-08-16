using Harbora.Domain.Common;

namespace Harbora.Domain.Monitoring;

/// <summary>
/// The collector's own memory of one container's restart counter, so an ever-climbing figure never
/// reaches the metrics table as anything but a delta.
///
/// <para>
/// Docker's <c>RestartCount</c> only ever climbs while a container keeps its identity, and starts
/// over at zero the moment the container is replaced — a redeploy, not a restart. Averaging that raw
/// figure over an hour would be exactly the summary <see cref="MetricRollup"/> was designed to refuse
/// for <c>net.rx</c>/<c>net.tx</c>, so it is never given the chance to: this row remembers the count
/// as of the last successful tick, the collector subtracts, and only the difference — computed by
/// <see cref="Harbora.Infrastructure.Monitoring.RestartDelta"/> — becomes a sample. Pure bookkeeping,
/// not a chart series in its own right: nothing reads <see cref="LastRestartCount"/> except the next
/// collector tick.
/// </para>
/// <para>
/// Durable rather than in-memory on purpose. The collector runs as a fresh scope on every tick, and a
/// gap here — a missed tick, a panel restart — must not lose whatever restarts happened during it: the
/// next successful tick diffs against whatever this row last held, however long ago that was, so a gap
/// is bridged rather than silently dropped.
/// </para>
/// </summary>
public class ContainerLifecycleCursor : BaseEntity
{
    public Guid ServerId { get; set; }

    /// <summary>The container name — the same key <c>cpu.percent</c>/<c>mem.used</c> samples use.</summary>
    public string ResourceRef { get; set; } = string.Empty;

    public int LastRestartCount { get; set; }

    /// <summary>When this reading was taken, so a stale cursor is at least inspectable.</summary>
    public DateTimeOffset ObservedAt { get; set; }
}
