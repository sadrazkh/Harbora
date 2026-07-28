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

    /// <summary>
    /// Encrypt artifacts before they leave the staging directory. Volume and database archives hold
    /// raw application data, so anyone who reaches the destination bucket/disk otherwise reads it in
    /// the clear. Existing unencrypted artifacts stay readable — the format is detected per file.
    /// </summary>
    public bool EncryptArchives { get; set; } = true;

    /// <summary>
    /// Keep a safety copy of the current volume before a destructive restore overwrites it, so a
    /// restore from a bad archive is itself recoverable. Disable only if disk is tight.
    /// </summary>
    public bool SnapshotBeforeRestore { get; set; } = true;
}
