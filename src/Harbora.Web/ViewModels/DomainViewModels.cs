namespace Harbora.Web.ViewModels;

/// <summary>
/// One row of the domains table.
///
/// <paramref name="CertificateExpiresAt"/> is nullable and stays that way: a domain with no
/// certificate yet has no expiry, and rendering "never" or a dash in that slot reads as a fact
/// about the certificate rather than the absence of one.
/// </summary>
public sealed record DomainRow(
    Guid Id, string Host, Guid AppId, string AppName, bool Ssl, bool Primary,
    Harbora.Domain.Common.CertificateStatus? CertificateStatus = null,
    DateTimeOffset? CertificateExpiresAt = null)
{
    /// <summary>Days left, or null when nothing was issued. Negative means already expired.</summary>
    public int? DaysLeft(DateTimeOffset now) =>
        CertificateExpiresAt is { } expiry ? (int)Math.Floor((expiry - now).TotalDays) : null;
}
