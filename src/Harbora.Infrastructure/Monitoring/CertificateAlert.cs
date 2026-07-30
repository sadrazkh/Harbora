using System.Globalization;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Networking;

namespace Harbora.Infrastructure.Monitoring;

/// <summary>An SSL alert worth sending, already worded.</summary>
public sealed record CertificateAlertMessage(AlertSeverity Severity, string Headline, string Detail);

/// <summary>
/// Decides whether a certificate's expiry is worth alerting about, and what to say.
///
/// Kept separate from the watcher so the threshold is testable without a background loop, a database
/// and a live TLS handshake — the same reason the domain and health verdicts are pure.
/// </summary>
public static class CertificateAlert
{
    public static CertificateAlertMessage? Evaluate(
        string host, string appName, DateTimeOffset? expiresAt, DateTimeOffset now)
    {
        // No certificate is not an expiry problem: the domain may be new, or pointed elsewhere. The
        // domain checker explains that case; duplicating it here would alert on brand-new domains.
        if (expiresAt is null) return null;

        var remaining = expiresAt.Value - now;

        // Let's Encrypt issues for 90 days and Traefik renews at 30 remaining, so a certificate still
        // inside this window means renewal is failing rather than pending. A healthy one never gets
        // here, which is what keeps this from becoming noise.
        if (remaining > DomainDiagnosis.RenewalWindow) return null;

        var day = expiresAt.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return remaining <= TimeSpan.Zero
            ? new(AlertSeverity.Critical, $"Certificate expired: {host}",
                  $"The certificate for {host} ({appName}) expired on {day}. Visitors are seeing a " +
                  "security warning right now.")
            : new(AlertSeverity.Warning, $"Certificate expiring: {host}",
                  $"The certificate for {host} ({appName}) expires in {(int)remaining.TotalDays} days, " +
                  $"on {day}. Renewal should already have happened, so check that port 80 is reachable " +
                  "and that DNS still points here.");
    }
}
