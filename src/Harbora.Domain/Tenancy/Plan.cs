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

    public bool IsDefault { get; set; }
    public bool IsEnabled { get; set; } = true;
}
