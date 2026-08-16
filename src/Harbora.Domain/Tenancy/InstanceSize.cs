using Harbora.Domain.Common;

namespace Harbora.Domain.Tenancy;

/// <summary>
/// A resource tier a customer can pick for an app (like a droplet/dyno size). Seeded with
/// built-ins; the provider can add custom sizes. An app's container CPU/memory limits are derived
/// from its chosen size, so tenants can only consume what their plan allows.
/// </summary>
public class InstanceSize : BaseEntity
{
    /// <summary>
    /// How long a key may be. Docker-ish and URL-safe, and short enough to read on a card.
    ///
    /// <para>
    /// It lives here rather than beside the normaliser that enforces it because the schema needs it
    /// too: <c>ServerInstanceOffer.InstanceSizeKey</c> is bounded to the same length so a key that
    /// fits in this column always fits there, and the data layer cannot reach into infrastructure to
    /// ask. Two constants would be free to drift, and the drift would truncate a key on one side of a
    /// join only.
    /// </para>
    /// </summary>
    public const int KeyMaxLength = 32;

    public string Key { get; set; } = string.Empty;      // "nano", "micro", "small"…
    public string Name { get; set; } = string.Empty;
    public string NameFa { get; set; } = string.Empty;

    /// <summary>
    /// Which kind of machine this tier is — general purpose, or weighted towards processor, memory or
    /// disk. Empty reads as general purpose, which is what every tier predating this column is; the
    /// vocabulary, the labels and that reading all live in <c>InstanceSizeFamily</c>.
    ///
    /// <para>
    /// A family belongs to the tier and not to the server hosting it, so a server's "optimised for
    /// memory" badge is derived from the tiers it offers rather than stored a second time where the
    /// two could disagree.
    /// </para>
    /// </summary>
    public string Family { get; set; } = string.Empty;

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

    /// <summary>
    /// What one hour of this size costs while it is running, in minor units.
    ///
    /// <para>
    /// <b>Null means nobody has priced this size; zero means it is deliberately free.</b> Note that
    /// this deliberately breaks the "zero means no ceiling" convention the limit columns above
    /// follow — a limit left blank is a decision not to limit, but a price left blank is not a
    /// decision to give it away. Collapsing the two is how an operator adds a size, forgets to
    /// price it, and hosts every workload on it for nothing for ever while each hourly tick reports
    /// success. Every size that predates billing is null, which is the truth about it.
    /// </para>
    /// </summary>
    public long? RunningRatePerHourMinor { get; set; }

    /// <summary>
    /// What one hour costs while the workload is stopped but not deleted — the reserved slot, the
    /// image and the port. Disk is charged separately per gibibyte, so this is only the slot.
    /// Null and zero mean what they mean on <see cref="RunningRatePerHourMinor"/>, and each state
    /// is resolved from its own column so pricing one does not vouch for the other.
    /// </summary>
    public long? StoppedRatePerHourMinor { get; set; }

    public bool IsBuiltIn { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int SortOrder { get; set; }
}
