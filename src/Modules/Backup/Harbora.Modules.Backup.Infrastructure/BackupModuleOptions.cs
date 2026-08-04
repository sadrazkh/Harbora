namespace Harbora.Modules.Backup.Infrastructure;

/// <summary>
/// Feature flags for the backup and sync modules.
///
/// <para>
/// All default to <c>false</c>. Enabling is a deliberate act by an operator who has read the
/// upgrade notes, not something that happens because a migration ran.
/// </para>
/// </summary>
public sealed class BackupFeatureOptions
{
    public const string SectionName = "Features";

    /// <summary>Backup module: repositories, policies, snapshots, restore.</summary>
    public bool Backup { get; set; }

    /// <summary>Sync module. Contracts only in this branch — nothing to enable yet.</summary>
    public bool Sync { get; set; }

    /// <summary>
    /// Always-on node that stores ciphertext it cannot read. Experimental: the guarantee comes from
    /// the sync engine's untrusted-device support rather than from Harbora, and the failure mode —
    /// the node quietly holding plaintext — is invisible from the UI.
    /// </summary>
    public bool EncryptedSyncNode { get; set; }

    /// <summary>Dispatch of backup jobs to enrolled remote devices.</summary>
    public bool RemoteBackupAgent { get; set; }
}

/// <summary>Where the Kopia binary is and how it is allowed to behave.</summary>
public sealed class KopiaOptions
{
    public const string SectionName = "Backups:Kopia";

    /// <summary>
    /// Resolved from PATH when left as the bare name. An absolute path is preferable in production:
    /// it removes any question of which binary on PATH was actually executed.
    /// </summary>
    public string BinaryPath { get; set; } = "kopia";

    /// <summary>
    /// Kopia's own config file. Given explicitly so concurrent operations on different repositories
    /// cannot collide over a shared default, and so nothing depends on the panel user's HOME.
    /// </summary>
    public string ConfigDirectory { get; set; } = "/var/lib/harbora/kopia";

    /// <summary>Content cache. On the same filesystem as the config directory by default.</summary>
    public string CacheDirectory { get; set; } = "/var/lib/harbora/kopia/cache";

    /// <summary>
    /// Ceiling on a single engine invocation. A hung process must not hold its target's lock
    /// forever — the job's own timeout is the outer bound, this is the inner one.
    /// </summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromHours(6);

    /// <summary>Shorter ceiling for commands that only read metadata (status, list).</summary>
    public TimeSpan MetadataCommandTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Bytes of engine output retained per invocation. Bounded because it is stored on a row and
    /// shown in a UI; a runaway stderr should not become a multi-megabyte column.
    /// </summary>
    public int MaxCapturedOutputBytes { get; set; } = 64 * 1024;
}

/// <summary>Settings for the module's own staging and restore areas.</summary>
public sealed class BackupModuleOptions
{
    public const string SectionName = "Backups:Module";

    /// <summary>
    /// Root that every restore destination must resolve inside unless the caller explicitly targets
    /// a docker volume or a database. The confinement check is what makes a hostile archive entry
    /// inert (THREAT_MODEL T2).
    /// </summary>
    public string RestoreRoot { get; set; } = "/var/lib/harbora/restore";

    /// <summary>Working area for archives being built or unpacked.</summary>
    public string StagingDirectory { get; set; } = "/var/lib/harbora/backups";

    /// <summary>
    /// Ceiling on what a single restore may expand to. An archive bomb is small on disk and
    /// enormous once extracted; without a bound, restoring one fills the server (THREAT_MODEL T8).
    /// </summary>
    public long MaxRestoreExpandedBytes { get; set; } = 512L * 1024 * 1024 * 1024;

    /// <summary>Ceiling on entry count, for the same reason.</summary>
    public long MaxRestoreEntryCount { get; set; } = 5_000_000;
}
