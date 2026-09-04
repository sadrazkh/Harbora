namespace Harbora.Infrastructure.Backups;

/// <summary>
/// The container-launch shape of turning WAL archiving on for one PostgreSQL instance (3.1, round-2
/// market-gaps plan) — the PITR counterpart of <c>DatabaseTls.ServerCommand</c>: both change what the
/// container is started WITH rather than anything issued to it once it is running, because
/// <c>wal_level</c> and <c>archive_mode</c> are PostgreSQL parameters that are only read at startup.
///
/// <para>
/// <c>archive_command</c> deliberately does not reach into object storage itself. The stock
/// <c>postgres</c> image has no S3 client, and giving it MinIO credentials on its command line would
/// put them in <c>ps</c> output and in every log that captures how the container was started — the
/// same reasoning that keeps every database password in this codebase out of a command line and in
/// its environment instead (see <see cref="DatabaseDumpPlan"/>'s own doc). So the command only ever
/// copies a finished segment into a docker volume mounted at <see cref="ArchiveMountPath"/>
/// (<see cref="VolumeNameFor"/>) — a panel-side shipper (a one-off container, the same seam every
/// other backup already runs through) is what actually uploads from there, matching the existing
/// "helper container touches the volume, the panel-side service owns the destination" split that
/// <see cref="BackupEngine.BackupVolumeAsync"/> already uses.
/// </para>
///
/// <para>
/// <c>test ! -f ... &amp;&amp; cp ...</c> rather than a bare <c>cp</c>: PostgreSQL's own docs require
/// <c>archive_command</c> to refuse (non-zero exit) if the destination file already exists with
/// different contents, and succeed if it already exists identically — because it retries the same
/// call after a crash. A bare overwrite would silently accept a short, crash-truncated segment
/// written over a complete one from an earlier attempt.
/// </para>
/// </summary>
public static class PostgresWalArchivingCommand
{
    /// <summary>Where <c>archive_command</c> writes inside the container, and where the shipper's
    /// own one-off container reads from.</summary>
    public const string ArchiveMountPath = "/wal_archive";

    /// <summary>A volume of its own, not the data volume — so a shipper reading it never races the
    /// database's own writes to <c>DataMountPath</c>, and pruning a shipped segment never touches
    /// anything PostgreSQL itself still owns.</summary>
    public static string VolumeNameFor(string serviceVolumeName) => $"{serviceVolumeName}-wal";

    /// <summary>
    /// The full command line for a PostgreSQL container with archiving on, given whatever the launch
    /// command already was (null, or <c>DatabaseTls.ServerCommand()</c>'s own <c>-c</c> arguments) —
    /// extended rather than replaced, the same "append, do not clobber" shape
    /// <c>ManagedServiceEngine.ProvisionAsync</c> already uses for Redis's memory-policy arguments.
    /// </summary>
    public static IReadOnlyList<string> Extend(IReadOnlyList<string>? existingCommand)
    {
        IReadOnlyList<string> baseCommand = existingCommand is { Count: > 0 } ? existingCommand : ["postgres"];

        return
        [
            .. baseCommand,
            "-c", "wal_level=replica",
            "-c", "archive_mode=on",
            "-c", $"archive_command=test ! -f {ArchiveMountPath}/%f && cp %p {ArchiveMountPath}/%f",
            // Forces a segment switch at least this often even on an idle database, so the
            // recoverable window's upper bound cannot drift arbitrarily far behind "now" purely
            // because nothing was written — see PitrRecoveryWindow for what reads this back.
            "-c", "archive_timeout=300"
        ];
    }
}
