using Harbora.Domain.Common;

namespace Harbora.Infrastructure.Monitoring;

/// <summary>One sample of one app's metric.</summary>
/// <param name="At">When it was taken.</param>
/// <param name="Percent">
/// Percentage of the app's allocation, or null when the sample exists but the allocation does not —
/// unmeasured is not zero, and it is certainly not "under the threshold".
/// </param>
public readonly record struct MetricSample(DateTimeOffset At, double? Percent);

/// <summary>
/// Whether a per-application threshold has actually been breached.
///
/// The rule that matters is the sustain window. A container's CPU touches 100% every time it starts,
/// and a memory figure spikes during a garbage collection — alerting on a single sample produces a
/// channel full of noise, which is worse than no channel because people mute it. So a breach must
/// hold for the whole window, and one measurement below the line inside that window clears it.
///
/// Unmeasured samples never contribute. A gap in collection is not a breach and not a recovery; it
/// is silence, and inventing either answer from it is the same lie as reporting a zero nobody read.
/// </summary>
public static class ThresholdRule
{
    /// <summary>
    /// True when every sample inside the window is at or above the threshold and the window is
    /// genuinely covered — an app that started two minutes ago cannot have breached a ten-minute
    /// sustain, however high its first sample was.
    /// </summary>
    /// <param name="samples">Recent samples for this app and metric, any order.</param>
    /// <param name="thresholdPercent">The line, as a percentage of the app's allocation.</param>
    /// <param name="sustain">How long it must hold. Zero means a single sample is enough.</param>
    /// <param name="now">Evaluation moment, supplied rather than read.</param>
    public static bool Breached(
        IEnumerable<MetricSample> samples, double thresholdPercent, TimeSpan sustain, DateTimeOffset now)
    {
        if (thresholdPercent <= 0) return false;

        var window = samples
            .Where(s => s.At <= now && s.At >= now - sustain)
            .OrderBy(s => s.At)
            .ToList();

        // Nothing to judge on. Silence is not a breach.
        if (window.Count == 0) return false;

        // An unmeasured sample inside the window is a gap, and a gap cannot sustain anything.
        if (window.Any(s => s.Percent is null)) return false;

        if (window.Any(s => s.Percent!.Value < thresholdPercent)) return false;

        // The window has to be covered, not merely touched. Without this, one sample taken a second
        // ago satisfies a ten-minute sustain — which is exactly the single-sample alert the sustain
        // exists to prevent.
        if (sustain > TimeSpan.Zero && window[0].At > now - sustain + Tolerance) return false;

        return true;
    }

    /// <summary>
    /// How much slack the window's start gets. The collector ticks on an interval, so demanding a
    /// sample at exactly `now - sustain` would mean the rule fires one tick late, every time.
    /// </summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Whether enough time has passed to say this again. A breach that persists is still one
    /// problem; repeating it every collector tick is how a channel becomes noise.
    /// </summary>
    public static bool MayRepeat(DateTimeOffset? lastFiredAt, DateTimeOffset now) =>
        lastFiredAt is not { } last || now - last >= RepeatAfter;

    /// <summary>An hour, which is long enough to stop a flood and short enough to still be a nag.</summary>
    public static readonly TimeSpan RepeatAfter = TimeSpan.FromHours(1);

    /// <summary>The severity a threshold breach carries — a warning, not an outage.</summary>
    public static AlertSeverity Severity => AlertSeverity.Warning;
}
