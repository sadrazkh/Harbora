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
    ///
    /// <para>
    /// <b>Null means nobody has priced this plan; zero means it has no floor on purpose.</b> Every
    /// rate on this class reads that way, and it is the opposite of the convention the caps above
    /// follow, where zero means unlimited. A cap left blank is a decision not to cap; a price left
    /// blank is not a decision to charge nothing. The two look identical in a column of zeros, and
    /// the difference is a bill nobody sends.
    /// </para>
    /// </summary>
    public long? BaseRatePerHourMinor { get; set; }

    /// <summary>
    /// Whether this plan sells capacity past its own caps. False keeps today's behaviour, where
    /// <c>IQuotaService</c> refuses; true lets the tenant past and charges the overage rates below.
    /// </summary>
    public bool AllowsOverage { get; set; }

    /// <summary>
    /// Charged per core-hour beyond <see cref="MaxCpuCores"/>. Only read when
    /// <see cref="AllowsOverage"/>. Null is unpriced; see <see cref="BaseRatePerHourMinor"/>.
    /// A plan that sells overage without pricing it gives the capacity away, which is the exact
    /// shape this nullability exists to make visible.
    /// </summary>
    public long? OverageCpuCoreHourMinor { get; set; }

    /// <summary>
    /// Charged per gibibyte-hour beyond <see cref="MaxMemoryBytes"/>. Only read when
    /// <see cref="AllowsOverage"/>. Null is unpriced; see <see cref="BaseRatePerHourMinor"/>.
    /// </summary>
    public long? OverageMemoryGbHourMinor { get; set; }

    /// <summary>
    /// Charged per gibibyte-hour beyond <see cref="MaxDiskBytes"/>. Only read when
    /// <see cref="AllowsOverage"/>. Null is unpriced; see <see cref="BaseRatePerHourMinor"/>.
    /// </summary>
    public long? OverageDiskGbHourMinor { get; set; }

    /// <summary>
    /// Charged per gibibyte-hour of allocated volume, inside the caps as well as past them.
    /// Null is unpriced; see <see cref="BaseRatePerHourMinor"/>.
    /// </summary>
    public long? DiskGbHourMinor { get; set; }

    public bool IsDefault { get; set; }
    public bool IsEnabled { get; set; } = true;
}
