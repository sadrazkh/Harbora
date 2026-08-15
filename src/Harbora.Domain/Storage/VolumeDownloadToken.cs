using Harbora.Domain.Common;

namespace Harbora.Domain.Storage;

/// <summary>
/// A one-time, self-expiring ticket that names exactly one file in one app's volume — what a
/// temporary download link is minted from.
///
/// <para>
/// Four rules make handing this to somebody with no panel session acceptable, and all four live on
/// this row rather than on the route that redeems it:
/// </para>
/// <list type="bullet">
/// <item><b>Single use.</b> <see cref="UsedAt"/> is set the instant the token is redeemed, and a
/// second attempt with the same value is refused — the same reason a forwarded link is bounded.</item>
/// <item><b>Self-expiring.</b> <c>AdminerSession.Lifetime</c> is what "temporary" means here too,
/// measured from <see cref="BaseEntity.CreatedAt"/> — the moment this row was minted — through the
/// exact same <c>AdminerSession.Expired</c> comparison an admin session is checked against. A
/// different span would be a different value; a second comparison would be a second, eventually
/// disagreeing, notion of expiry, so there is no <c>ExpiresAt</c> column here to drift from it.</item>
/// <item><b>The path is fixed at mint time.</b> <see cref="Path"/> is written once, when the row is
/// created, and nothing about redeeming a token can change it — the volume-path defect this project
/// has already fixed twice.</item>
/// <item><b>It belongs to one app.</b> <see cref="AppId"/> and <see cref="VolumeId"/> are set from
/// values the minting caller already resolved through that app's own tenant-filtered collection, so
/// the pairing can only ever have named a volume its own workspace could already see.</item>
/// </list>
/// <para>
/// Only the hash is stored, the same reason <c>PasswordResetToken</c> and <c>NodeEnrollmentToken</c>
/// do it: the value is only ever compared, so keeping it costs nothing and a database dump must not
/// become a list of working links.
/// </para>
/// </summary>
public class VolumeDownloadToken : BaseEntity
{
    public Guid AppId { get; set; }
    public Guid VolumeId { get; set; }

    /// <summary>The normalised path inside the volume this token names. Fixed at mint time.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>SHA-256 of the token handed out in the link, hex, lowercase.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>Set the moment the token is redeemed. A second redemption is refused.</summary>
    public DateTimeOffset? UsedAt { get; set; }

    public bool IsSpent => UsedAt is not null;
}
