using Harbora.Domain.Common;

namespace Harbora.Domain.Tenancy;

/// <summary>
/// A tenancy plan the provider offers to customers. Caps how much a workspace may consume in
/// total; quota checks run before an app/service is created or deployed. Zero means "unlimited".
/// </summary>
public class Plan : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string NameFa { get; set; } = string.Empty;

    public int MaxApps { get; set; }
    public int MaxServices { get; set; }

    /// <summary>Total memory a workspace may commit across all its apps (0 = unlimited).</summary>
    public long MaxMemoryBytes { get; set; }
    /// <summary>Total CPU cores a workspace may commit (0 = unlimited).</summary>
    public double MaxCpuCores { get; set; }
    /// <summary>Total volume/disk a workspace may allocate (0 = unlimited).</summary>
    public long MaxDiskBytes { get; set; }

    /// <summary>Comma-separated instance-size keys this plan allows (empty = all enabled sizes).</summary>
    public string AllowedSizeKeys { get; set; } = string.Empty;

    /// <summary>Optional server-pool tag: apps in this plan may only be scheduled on matching nodes.</summary>
    public string? NodePool { get; set; }

    /// <summary>For display/billing; not charged by Harbora itself.</summary>
    public decimal MonthlyPrice { get; set; }

    /// <summary>
    /// The floor. A workspace on this plan pays at least this much per hour, whatever it is running
    /// — including nothing. <see cref="MonthlyPrice"/> is unrelated and remains display-only.
    /// </summary>
    public long BaseRatePerHourMinor { get; set; }

    /// <summary>
    /// Whether this plan sells capacity past its own caps. False keeps today's behaviour, where
    /// <c>IQuotaService</c> refuses; true lets the tenant past and charges the overage rates below.
    /// </summary>
    public bool AllowsOverage { get; set; }

    /// <summary>
    /// Charged per core-hour beyond <see cref="MaxCpuCores"/>. Only read when
    /// <see cref="AllowsOverage"/>.
    /// </summary>
    public long OverageCpuCoreHourMinor { get; set; }

    /// <summary>
    /// Charged per gibibyte-hour beyond <see cref="MaxMemoryBytes"/>. Only read when
    /// <see cref="AllowsOverage"/>.
    /// </summary>
    public long OverageMemoryGbHourMinor { get; set; }

    /// <summary>
    /// Charged per gibibyte-hour beyond <see cref="MaxDiskBytes"/>. Only read when
    /// <see cref="AllowsOverage"/>.
    /// </summary>
    public long OverageDiskGbHourMinor { get; set; }

    /// <summary>Charged per gibibyte-hour of allocated volume, inside the caps as well as past them.</summary>
    public long DiskGbHourMinor { get; set; }

    public bool IsDefault { get; set; }
    public bool IsEnabled { get; set; } = true;
}
