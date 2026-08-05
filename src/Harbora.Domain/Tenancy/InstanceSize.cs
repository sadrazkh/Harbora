using Harbora.Domain.Common;

namespace Harbora.Domain.Tenancy;

/// <summary>
/// A resource tier a customer can pick for an app (like a droplet/dyno size). Seeded with
/// built-ins; the provider can add custom sizes. An app's container CPU/memory limits are derived
/// from its chosen size, so tenants can only consume what their plan allows.
/// </summary>
public class InstanceSize : BaseEntity
{
    public string Key { get; set; } = string.Empty;      // "nano", "micro", "small"…
    public string Name { get; set; } = string.Empty;
    public string NameFa { get; set; } = string.Empty;

    public double CpuCores { get; set; }                 // e.g. 0.25, 0.5, 1, 2
    public long MemoryBytes { get; set; }

    /// <summary>
    /// How much disk this tier comes with. Zero means no ceiling, as a zero does on every other
    /// limit here.
    ///
    /// A size used to be CPU and memory only, so every picker offered "1 vCPU / 1 GB" and said
    /// nothing about storage — which is the figure people actually run out of. It is a ceiling on
    /// what the instance's own volumes are measured to hold: a Docker volume has no size of its
    /// own, so nothing can stop a process writing, and what can be done is refuse to hand out a
    /// tier smaller than what is already on disk.
    /// </summary>
    public long DiskBytes { get; set; }

    public bool IsBuiltIn { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int SortOrder { get; set; }
}
