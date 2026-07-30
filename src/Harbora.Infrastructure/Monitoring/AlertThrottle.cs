using System.Collections.Concurrent;

namespace Harbora.Infrastructure.Monitoring;

/// <summary>
/// Keeps a recurring condition from alerting on every tick, per subject.
///
/// The disk warning previously used a <c>static</c> field on the collector, which is scoped: one
/// timestamp shared by every server and every workspace, so a full disk on one node silenced the
/// warning for all the others for an hour. Keyed here instead, and held by a singleton because the
/// collector itself is rebuilt on each pass.
///
/// Best-effort by design: the state is in memory, so a restart allows one extra alert. That is the
/// right trade — losing a warning matters, repeating one does not.
/// </summary>
public sealed class AlertThrottle
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastFired = new(StringComparer.Ordinal);

    /// <summary>
    /// True if this subject has not alerted within <paramref name="interval"/>, recording the attempt.
    /// </summary>
    public bool ShouldFire(string key, DateTimeOffset now, TimeSpan interval)
    {
        var fired = false;
        _lastFired.AddOrUpdate(key,
            _ => { fired = true; return now; },
            (_, previous) =>
            {
                if (now - previous < interval) return previous;
                fired = true;
                return now;
            });
        return fired;
    }
}
