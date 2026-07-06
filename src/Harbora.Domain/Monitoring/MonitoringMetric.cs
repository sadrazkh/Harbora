using Harbora.Domain.Common;

namespace Harbora.Domain.Monitoring;

/// <summary>
/// A time-series sample. Kept deliberately generic (name + labels + value) so the
/// monitoring engine can record host and per-container metrics through one table.
/// A retention job trims old rows.
/// </summary>
public class MonitoringMetric : BaseEntity
{
    public Guid ServerId { get; set; }
    public string Name { get; set; } = string.Empty;      // cpu.percent, mem.used, disk.used …
    public string? ResourceRef { get; set; }              // container id / app slug, null = host
    public double Value { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}
