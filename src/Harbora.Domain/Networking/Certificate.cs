using Harbora.Domain.Common;

namespace Harbora.Domain.Networking;

/// <summary>
/// Metadata about a TLS certificate. Traefik owns the actual ACME material in acme.json;
/// this row lets Harbora surface issuance status and expiry warnings in monitoring.
/// </summary>
public class Certificate : BaseEntity
{
    public string Host { get; set; } = string.Empty;
    public CertificateStatus Status { get; set; } = CertificateStatus.Pending;
    public string Issuer { get; set; } = "Let's Encrypt";
    public DateTimeOffset? IssuedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? LastError { get; set; }
}
