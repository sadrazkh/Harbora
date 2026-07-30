using System.Globalization;
using Harbora.Application.Abstractions;

namespace Harbora.Infrastructure.Networking;

/// <summary>
/// Turns the facts gathered about a domain into a verdict and one concrete next step.
///
/// Kept pure and separate from the probing so the wording and the ordering of causes are testable.
/// The ordering matters: DNS is checked before the certificate, because "no certificate" is a
/// symptom when DNS is wrong, and telling someone to wait for a certificate that can never be issued
/// wastes their afternoon.
/// </summary>
public static class DomainDiagnosis
{
    /// <summary>A certificate this close to expiry is reported even while still technically valid.</summary>
    public static readonly TimeSpan RenewalWindow = TimeSpan.FromDays(14);

    /// <summary>A calendar the message's English wording matches, regardless of the UI culture.</summary>
    private static string Day(DateTimeOffset value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DomainStatus Diagnose(string host, DomainProbe probe, DateTimeOffset now)
    {
        if (probe.Error is { Length: > 0 })
            return new(host, DomainReadiness.Unknown,
                "The check couldn't complete.", probe.Error, probe);

        if (probe.ResolvedIps.Count == 0)
            return new(host, DomainReadiness.DnsMissing,
                $"{host} doesn't resolve.",
                probe.ExpectedIps.Count > 0
                    ? $"Add a DNS A record for {host} pointing to {string.Join(" or ", probe.ExpectedIps)}."
                    : $"Add a DNS A record for {host} pointing to this server.",
                probe);

        // Any overlap counts: a domain behind round-robin DNS or a CDN may resolve to several
        // addresses, only one of which needs to be this server.
        var pointsHere = probe.ExpectedIps.Count == 0 ||
                         probe.ResolvedIps.Intersect(probe.ExpectedIps, StringComparer.OrdinalIgnoreCase).Any();

        if (!pointsHere)
            return new(host, DomainReadiness.DnsNotPointingHere,
                $"{host} resolves to {string.Join(", ", probe.ResolvedIps)}, which isn't this server.",
                $"Point its A record at {string.Join(" or ", probe.ExpectedIps)}. " +
                "Until then no certificate can be issued, because the HTTP-01 challenge is answered here.",
                probe);

        if (!probe.HttpsAnswered)
            return new(host, DomainReadiness.Unreachable,
                $"DNS is correct, but nothing answered on HTTPS.",
                "Check that port 443 is open on the server and that the app is running. " +
                "A brand-new domain can also take a minute while the certificate is requested.",
                probe);

        if (probe.CertificateExpiresAt is null)
            return new(host, DomainReadiness.AwaitingCertificate,
                "HTTPS answered but without a usable certificate.",
                "Let's Encrypt is most likely still working. If it persists, check that port 80 is " +
                "reachable — the HTTP-01 challenge needs it.",
                probe);

        if (probe.CertificateExpiresAt <= now)
            return new(host, DomainReadiness.AwaitingCertificate,
                $"The certificate expired on {Day(probe.CertificateExpiresAt.Value)}.",
                "Renewal is automatic; if it hasn't happened, check that port 80 is still reachable.",
                probe);

        var daysLeft = (int)(probe.CertificateExpiresAt.Value - now).TotalDays;
        if (probe.CertificateExpiresAt - now <= RenewalWindow)
            return new(host, DomainReadiness.Ready,
                $"Serving over HTTPS. Certificate renews soon ({daysLeft} days left).",
                null, probe);

        return new(host, DomainReadiness.Ready,
            $"Serving over HTTPS. Certificate valid for {daysLeft} more days" +
            (probe.CertificateIssuer is { Length: > 0 } ? $" ({probe.CertificateIssuer})." : "."),
            null, probe);
    }
}
