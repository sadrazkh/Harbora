using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Harbora.Domain.Common;

namespace Harbora.Infrastructure.Backups;

/// <summary>
/// The two identities one backup run has: the target no second run of it may run beside, and
/// itself.
///
/// <para>
/// Pure and deterministic on purpose. <see cref="ExclusionKeyFor"/> is written onto the
/// <c>Job</c> row at enqueue and compared, later and possibly by another process, against jobs
/// enqueued by something else entirely — a scheduler tick against a form post — so it cannot come
/// from anything this process happens to hold.
/// </para>
/// </summary>
public static class BackupRunIdentity
{
    /// <summary>
    /// What no two concurrently running backups may share.
    ///
    /// <para>
    /// A <c>Backup</c> row is created per run, so a backup's own id is exactly as useless for this
    /// as a <c>Deployment</c>'s: two backups of one target are two different <c>TargetId</c>s and
    /// <c>InFlightTargets</c> does not hold them apart. A deployment can pass its app's id because
    /// the app is a row someone already has. A backup's target is not a row — it is a
    /// <see cref="BackupType"/> and a reference — so the value is derived from that pair instead.
    /// </para>
    /// <para>
    /// <b>The workspace is deliberately not part of it.</b> A full-platform backup's reference is
    /// the literal "platform" for every workspace, and the artifact it stages is
    /// <c>platform-{stamp}.json.gz</c> — which carries no workspace either. Two tenants' platform
    /// backups therefore claim one path in the shared staging volume, and keying on the type and
    /// reference alone is what keeps them from doing it at the same time. Everywhere else the
    /// reference is already unique (a service id, an app id, a docker volume name, which is itself
    /// global on a daemon), so all this costs is that one over-serialisation — and over-serialising
    /// is the safe direction to be wrong in.
    /// </para>
    /// </summary>
    /// <param name="targetRef">
    /// The reference as it was stored or posted. Trimmed and lower-cased before hashing: it arrives
    /// from a form and from a stored schedule, and two spellings of one target must not be allowed
    /// to run at once.
    /// </param>
    public static Guid ExclusionKeyFor(BackupType type, string? targetRef)
    {
        // Separated rather than concatenated: the type is always the digits before the first colon,
        // so no reference can be spelled in a way that makes two different pairs hash to one key.
        var canonical = $"{(int)type}:{(targetRef ?? string.Empty).Trim().ToLowerInvariant()}";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));

        // The first 128 bits of a SHA-256, read as a Guid. Not a UUID of any version and never
        // presented as one — it exists only to be compared with itself.
        return new Guid(digest.AsSpan(0, 16));
    }

    /// <summary>
    /// What goes into the staged artifact's filename, and what makes it this run's rather than this
    /// second's.
    ///
    /// <para>
    /// The time leads, because a listing of the staging volume sorting chronologically is the whole
    /// reason the stamp is a stamp. <c>InvariantCulture</c> because this is a FILENAME and the
    /// panel's default culture is Persian — the ambient calendar would write Jalali years into
    /// artifact names, inconsistently with backups taken from a background job, and unsortably.
    /// </para>
    /// <para>
    /// The run's own id follows, and it is the guard that survives leaving this process. Per-target
    /// exclusion is held in memory by the worker, so it is a promise about one panel; at one-second
    /// resolution two runs of one target claimed the same path, and two helper containers would
    /// each write it. Whichever finished second was the file both runs then checksummed, uploaded
    /// and recorded <c>Completed</c> — an archive holding two moments of the data with nothing
    /// saying so. Eight hex digits of the row's id separate runs of one target comfortably and
    /// leave the name readable.
    /// </para>
    /// </summary>
    public static string StampFor(DateTimeOffset at, Guid backupId) =>
        at.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + backupId.ToString("N")[..8];
}
