using Harbora.Domain.Common;

namespace Harbora.Domain.Monitoring;

/// <summary>How much time one summarised row covers.</summary>
public enum RollupPeriod
{
    Hour = 0,
    Day = 1
}

/// <summary>
/// A summary of one metric over one period.
///
/// Raw samples are kept for a day and then deleted, which makes "was this creeping up all week?"
/// unanswerable — and that is the shape of most real capacity problems. Keeping every sample for a
/// year instead would grow without bound, so completed periods are summarised and the raw points let
/// go.
///
/// <see cref="Minimum"/> and <see cref="Maximum"/> are stored, not just the average, because the
/// spike is usually the thing being looked for and an average is exactly what hides it.
/// </summary>
public class MetricRollup : BaseEntity
{
    public Guid ServerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ResourceRef { get; set; }

    public RollupPeriod Period { get; set; }

    /// <summary>Start of the period, truncated to the hour or the day it covers.</summary>
    public DateTimeOffset PeriodStart { get; set; }

    public double Minimum { get; set; }
    public double Maximum { get; set; }
    public double Average { get; set; }

    /// <summary>
    /// How many samples went into it. Kept so periods can be combined correctly: an average of
    /// averages is wrong whenever the periods hold different numbers of samples.
    /// </summary>
    public int SampleCount { get; set; }
}
