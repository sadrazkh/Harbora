using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Harbora.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Networking;

/// <summary>
/// Gathers the facts <see cref="DomainDiagnosis"/> interprets: where the name resolves, and what
/// certificate the server actually presents for it.
///
/// The certificate is read by completing a real TLS handshake with SNI rather than trusting our own
/// records — the question being answered is "what does a browser get?", and only the live handshake
/// answers that. Validation is deliberately not enforced during the probe: an expired or untrusted
/// certificate is exactly the condition worth reporting, so it must be inspected, not rejected.
///
/// "This server's addresses" are taken from the panel's own domain. It avoids depending on an
/// external IP service, and it is the right comparison anyway: a custom domain should land wherever
/// the panel lands.
/// </summary>
public sealed class DomainInspector(ISystemClock clock, ILogger<DomainInspector> logger) : IDomainInspector
{
    /// <summary>Kept short: this runs while someone waits on a page.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(6);

    public async Task<DomainStatus> InspectAsync(string host, CancellationToken ct)
    {
        host = host.Trim().TrimEnd('.');

        IReadOnlyList<string> resolved = [];
        IReadOnlyList<string> expected = [];
        string? error = null;

        try
        {
            resolved = await ResolveAsync(host, ct);
            expected = await ResolveAsync(PanelHost(), ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "DNS lookup failed for {Host}.", host);
            error = ex.Message;
        }

        var (answered, subject, issuer, expires, probeError) = error is null && resolved.Count > 0
            ? await ProbeTlsAsync(host, ct)
            : (false, null, null, (DateTimeOffset?)null, null);

        var probe = new DomainProbe(resolved, expected, answered, subject, issuer, expires, error ?? probeError);
        return DomainDiagnosis.Diagnose(host, probe, clock.UtcNow);
    }

    /// <summary>The panel's own hostname, which is where a correctly-pointed domain should also land.</summary>
    private static string PanelHost() =>
        Environment.GetEnvironmentVariable("PANEL_DOMAIN") is { Length: > 0 } panel
            ? panel.Trim().TrimEnd('.')
            : "localhost";

    private static async Task<IReadOnlyList<string>> ResolveAsync(string host, CancellationToken ct)
    {
        // An address written directly as a host needs no lookup.
        if (IPAddress.TryParse(host, out var literal)) return [literal.ToString()];

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            var entries = await Dns.GetHostAddressesAsync(host, timeout.Token);
            return entries
                .Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                .Select(a => a.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (SocketException)
        {
            // NXDOMAIN and friends are an answer, not a failure of the check.
            return [];
        }
    }

    private async Task<(bool Answered, string? Subject, string? Issuer, DateTimeOffset? Expires, string? Error)>
        ProbeTlsAsync(string host, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);

            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, 443, timeout.Token);

            X509Certificate2? presented = null;
            // The callback belongs in exactly one place: supplying it both here and in the
            // authentication options makes SslStream throw. Accept whatever is presented so it can be
            // reported — refusing would turn "your certificate expired", the thing this exists to say,
            // into a bare connection error.
            await using var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false,
                (_, cert, _, _) =>
                {
                    if (cert is not null) presented = new X509Certificate2(cert);
                    return true;
                });

            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host   // SNI: without it Traefik serves its default certificate
            }, timeout.Token);

            if (presented is null) return (true, null, null, null, null);

            using (presented)
            {
                var (subject, issuer, expires) = Interpret(
                    presented.Subject, presented.Issuer,
                    new DateTimeOffset(presented.NotAfter.ToUniversalTime(), TimeSpan.Zero));
                return (true, subject, issuer, expires, null);
            }
        }
        catch (Exception ex) when (ProbeFailures.IsConnectionFailure(ex))
        {
            // The domain genuinely didn't answer. That is a verdict, not an error.
            logger.LogDebug(ex, "TLS probe found nothing answering for {Host}.", host);
            return (false, null, null, null, null);
        }
        catch (Exception ex)
        {
            // Anything else is our bug, not the user's domain. Reporting it as "nothing answered"
            // is how a broken probe spent a day looking like a broken deployment.
            logger.LogWarning(ex, "The TLS probe for {Host} failed unexpectedly.", host);
            return (false, null, null, null, $"The certificate check failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads a presented certificate the way the panel should report it.
    ///
    /// Traefik answers with a self-signed default certificate for any host it has no real one for, so
    /// the handshake succeeds and a naive reading would call that "valid until 2027". It means "no
    /// certificate for this host yet" — reported as no expiry, which is the case the diagnosis already
    /// knows how to explain.
    /// </summary>
    public static (string? Subject, string? Issuer, DateTimeOffset? Expires) Interpret(
        string subjectDn, string issuerDn, DateTimeOffset notAfter)
    {
        var issuer = ShortName(issuerDn);
        var isDefault = issuer.Contains("TRAEFIK", StringComparison.OrdinalIgnoreCase);

        return (ShortName(subjectDn),
                isDefault ? null : issuer,
                isDefault ? null : notAfter);
    }

    /// <summary>Pulls CN (or O) out of an X.500 name so the UI shows "Let's Encrypt", not a DN.</summary>
    public static string ShortName(string distinguishedName)
    {
        foreach (var prefix in new[] { "CN=", "O=" })
        {
            var part = distinguishedName.Split(',')
                .Select(p => p.Trim())
                .FirstOrDefault(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (part is not null) return part[prefix.Length..].Trim();
        }
        return distinguishedName;
    }
}
