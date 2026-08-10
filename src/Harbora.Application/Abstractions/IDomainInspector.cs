namespace Harbora.Application.Abstractions;

/// <summary>How ready a domain actually is, as opposed to how it was configured.</summary>
public enum DomainReadiness
{
    /// <summary>DNS points here, HTTPS answers, and the certificate is valid.</summary>
    Ready = 0,
    /// <summary>DNS is correct but the certificate isn't issued (or valid) yet.</summary>
    AwaitingCertificate = 1,
    /// <summary>DNS does not point at this server, so a certificate can never be issued.</summary>
    DnsNotPointingHere = 2,
    /// <summary>The name does not resolve at all.</summary>
    DnsMissing = 3,
    /// <summary>DNS is right but nothing answered — ports blocked, or the app is down.</summary>
    Unreachable = 4,
    /// <summary>The check itself could not run.</summary>
    Unknown = 5
}

/// <summary>Raw facts gathered about a domain, before they are interpreted.</summary>
public sealed record DomainProbe(
    IReadOnlyList<string> ResolvedIps,
    IReadOnlyList<string> ExpectedIps,
    bool HttpsAnswered,
    string? CertificateSubject,
    string? CertificateIssuer,
    DateTimeOffset? CertificateExpiresAt,
    string? Error = null,
    bool CertificateValid = true);

/// <summary>A verdict plus the one thing the user should do about it.</summary>
public sealed record DomainStatus(
    string Host,
    DomainReadiness Readiness,
    string Summary,
    string? Action,
    DomainProbe Probe)
{
    public bool IsReady => Readiness == DomainReadiness.Ready;
}

/// <summary>
/// Checks whether a custom domain is genuinely serving, rather than merely configured.
/// The panel otherwise shows "SSL" because the box was ticked, while the browser shows a
/// certificate error — the single most common support question a hosting platform gets.
/// </summary>
public interface IDomainInspector
{
    Task<DomainStatus> InspectAsync(string host, CancellationToken ct);
}
