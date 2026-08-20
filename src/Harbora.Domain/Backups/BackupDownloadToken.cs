using Harbora.Domain.Common;

namespace Harbora.Domain.Backups;

/// <summary>
/// A one-time, self-expiring ticket that names exactly one completed <see cref="Backup"/> — what a
/// self-serve database export's temporary download link is minted from.
///
/// <para>
/// Sub-project 10: the same shape as <c>Harbora.Domain.Storage.VolumeDownloadToken</c> (D4), reused
/// rather than reinvented, because the four rules that make handing a link to somebody with no panel
/// session acceptable do not depend on what the link points at. What differs is only the thing this
/// row names — a <see cref="Backup"/>'s artifact rather than an app volume's file — so this is its
/// own small type rather than a strained reuse of <c>VolumeDownloadToken</c>'s <c>AppId</c>/
/// <c>VolumeId</c>/<c>Path</c> fields, which have no honest value for a backup artifact.
/// </para>
/// <list type="bullet">
/// <item><b>Single use.</b> <see cref="UsedAt"/> is set the instant the token is redeemed, and a
/// second attempt with the same value is refused.</item>
/// <item><b>Self-expiring.</b> <c>AdminerSession.Lifetime</c> — the platform's own vocabulary for
/// temporary access — measured from <see cref="BaseEntity.CreatedAt"/>, the same comparison an
/// Adminer session and a volume download token are checked against. There is no <c>ExpiresAt</c>
/// column here to drift from that shared definition.</item>
/// <item><b>The backup is fixed at mint time.</b> <see cref="BackupId"/> is written once and nothing
/// about redeeming a token can change it.</item>
/// <item><b>It belongs to one workspace.</b> <see cref="BackupId"/> is set from a <see cref="Backup"/>
/// the minting caller already resolved through that workspace's own tenant-filtered collection, so
/// the pairing can only ever have named a backup its own workspace could already see.</item>
/// </list>
/// <para>
/// Only the hash is stored, the same reason <c>VolumeDownloadToken</c> and <c>PasswordResetToken</c>
/// do it: the value is only ever compared, so keeping it costs nothing and a database dump must not
/// become a list of working links.
/// </para>
/// </summary>
public class BackupDownloadToken : BaseEntity
{
    public Guid BackupId { get; set; }

    /// <summary>SHA-256 of the token handed out in the link, hex, lowercase.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>Set the moment the token is redeemed. A second redemption is refused.</summary>
    public DateTimeOffset? UsedAt { get; set; }

    public bool IsSpent => UsedAt is not null;
}
