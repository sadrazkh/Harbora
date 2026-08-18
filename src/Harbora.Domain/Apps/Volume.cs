using Harbora.Domain.Common;

namespace Harbora.Domain.Apps;

/// <summary>A persistent volume mounted into the app's containers.</summary>
public class Volume : BaseEntity
{
    public Guid AppId { get; set; }
    public App? App { get; set; }

    public string Name { get; set; } = string.Empty;   // docker volume name
    public string MountPath { get; set; } = string.Empty; // path inside container
    public bool ReadOnly { get; set; }
    public long? SizeLimitBytes { get; set; }

    /// <summary>
    /// HARBORA-0033. When set, every path that would destroy this volume's data — the single-volume
    /// delete, an app delete with its volumes, a preview teardown, a project's cascading delete —
    /// refuses before touching anything and names this flag as the reason. See
    /// <c>Harbora.Infrastructure.Storage.VolumeProtection</c> for the one place that rule is enforced.
    ///
    /// Deliberately does not block detaching a volume without deleting its data: that leaves the bytes
    /// on the server exactly as an unprotected unmount does, so it is not "destroying" anything this
    /// flag is about.
    /// </summary>
    public bool Protected { get; set; }

    /// <summary>
    /// What was on it when it was last measured, and when. Stored rather than read on demand,
    /// because measuring walks the whole directory — and kept with its timestamp because a figure
    /// with no date beside it is trusted for longer than it deserves.
    /// </summary>
    public long? StorageBytes { get; set; }
    public DateTimeOffset? StorageMeasuredAt { get; set; }
}
