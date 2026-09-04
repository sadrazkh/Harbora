using Harbora.Domain.Common;
using Harbora.Infrastructure.Services;

namespace Harbora.Infrastructure.Backups;

/// <summary>
/// A PostgreSQL physical base backup — <c>pg_basebackup</c>'s own tar stream, not a logical dump
/// (3.1, round-2 market-gaps plan). The anchor point-in-time recovery replays WAL forward from;
/// <see cref="Harbora.Domain.Common.BackupType.PostgresBaseBackup"/> is what
/// <see cref="BackupEngine"/> schedules, stores, retains and delivers it under, reusing every one of
/// those exactly as <see cref="DatabaseDumpPlan"/> already does for a logical dump — the same
/// <c>Backup</c>/<c>BackupSchedule</c>/<c>BackupDestination</c> rows, the same encryption, checksum
/// and delivery, the same retention pass. Only the command that produces the artifact is new.
///
/// <para>
/// <c>--wal-method=none</c>: this platform's base backups deliberately do not also stream WAL over
/// the backup connection, because continuous archiving (<see cref="PostgresWalArchivingCommand"/>)
/// already covers every segment from the backup's own start label onward — asking
/// <c>pg_basebackup</c> to duplicate that would store the same bytes twice for no additional safety.
/// This is why a base backup on its own, with WAL archiving OFF, is not really a restorable PITR
/// anchor — see <c>PitrRecoveryWindow</c>, which refuses to report a window at all unless archiving
/// is both configured and has actually shipped something.
/// </para>
/// </summary>
public static class PostgresBaseBackupPlan
{
    /// <summary>The plan for this engine, or null — point-in-time recovery is PostgreSQL-only
    /// (<see cref="PitrSupport"/>), so there is no base-backup command for anything else.</summary>
    public static DumpPlan? For(ManagedServiceType type, ServiceCreds creds, string targetPath)
    {
        if (!PitrSupport.Supports(type)) return null;

        return new DumpPlan(
            ["sh", "-c",
             // -D - -Ft -z: a single gzip-compressed tar stream to stdout — no on-disk staging
             // inside the container, and no external gzip needed the way DatabaseDumpPlan's pg_dump
             // command uses one, because pg_basebackup already compresses its own stream with -z.
             // --checkpoint=fast: starts the backup immediately rather than waiting for the next
             // scheduled checkpoint, at the cost of a brief I/O spike — the right trade for a backup
             // an operator is waiting on rather than one running silently overnight, and this
             // platform runs both through the same schedule so there is only one choice to make.
             $"set -o pipefail; pg_basebackup -h {Quote(creds.Host)} -p {creds.Port} -U {Quote(creds.User)} " +
             $"-D - -Ft -z --wal-method=none --checkpoint=fast --no-password > {Quote(targetPath)}"],
            new Dictionary<string, string> { ["PGPASSWORD"] = creds.Password },
            ".tar.gz");
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
