namespace Harbora.Infrastructure.Backups;

public sealed class BackupOptions
{
    /// <summary>
    /// Local staging directory — also a docker named volume (harbora_backups) mounted into both
    /// the panel and the one-off tar containers, so produced artifacts are visible to both.
    /// </summary>
    public string StagingDir { get; set; } = "/var/lib/harbora/backups";

    /// <summary>Named docker volume backing <see cref="StagingDir"/> (used when wiring one-off containers).</summary>
    public string StagingVolume { get; set; } = "harbora_backups";

    /// <summary>Alpine image used to tar/untar volumes.</summary>
    public string HelperImage { get; set; } = "alpine:3.20";

    /// <summary>Default retention when a manual backup doesn't specify one.</summary>
    public int DefaultRetentionCount { get; set; } = 7;
}
