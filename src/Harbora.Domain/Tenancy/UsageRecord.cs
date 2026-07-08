using Harbora.Domain.Common;

namespace Harbora.Domain.Tenancy;

/// <summary>
/// Accumulated resource usage for a workspace in one billing period (month). The metering service
/// samples committed resources periodically and adds resource-hours — the basis for usage invoices.
/// </summary>
public class UsageRecord : BaseEntity
{
    public Guid WorkspaceId { get; set; }

    /// <summary>First day of the billing month.</summary>
    public DateOnly Period { get; set; }

    public double MemoryGbHours { get; set; }
    public double CpuCoreHours { get; set; }
    public int AppCountPeak { get; set; }
}
