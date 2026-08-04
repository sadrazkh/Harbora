using System.Security.Cryptography.X509Certificates;
using Harbora.Infrastructure.Nodes;
using Microsoft.Extensions.Options;

namespace Harbora.Web.Infrastructure;

/// <summary>
/// Finds the client certificate a node presented, whether TLS was terminated by Kestrel or by
/// Traefik in front of it.
///
/// <para>
/// The forwarded path is off unless an operator turns it on, because a certificate header is only
/// authentication when something upstream guarantees it. Traefik must both <em>require</em> a client
/// certificate on the node router and overwrite the header on every request; without that, a header
/// anyone can set is a login form with no password field. The flag is the operator asserting they
/// did the first part.
/// </para>
/// </summary>
public sealed class NodeClientCertificateResolver(
    IOptions<NodeAgentControlPlaneOptions> options,
    ILogger<NodeClientCertificateResolver> log)
{
    /// <summary>What Traefik's <c>passTLSClientCert</c> middleware sets when configured with <c>pem: true</c>.</summary>
    public const string ForwardedHeader = "X-Forwarded-Tls-Client-Cert";

    private readonly NodeAgentControlPlaneOptions _options = options.Value;

    /// <summary>The presented certificate, or null when there is none this panel is willing to believe.</summary>
    public X509Certificate2? Resolve(HttpContext context)
    {
        // The connection's own certificate first: when Kestrel terminated the TLS itself, this is
        // the real thing and no header can override it.
        if (context.Connection.ClientCertificate is { } direct) return direct;

        if (!_options.TrustForwardedClientCertificate) return null;

        if (!context.Request.Headers.TryGetValue(ForwardedHeader, out var forwarded)) return null;

        var raw = forwarded.ToString();
        if (string.IsNullOrWhiteSpace(raw)) return null;

        try
        {
            return Parse(raw);
        }
        catch (Exception e) when (e is FormatException or System.Security.Cryptography.CryptographicException)
        {
            log.LogWarning(e, "A forwarded client certificate header was present but could not be parsed.");
            return null;
        }
    }

    /// <summary>
    /// Traefik has sent this header in three shapes across versions: URL-encoded PEM, PEM with the
    /// armour lines, and bare base64 DER. Accepting all three is cheaper than pinning a version in
    /// a document nobody reads before upgrading.
    /// </summary>
    public static X509Certificate2 Parse(string headerValue)
    {
        var decoded = Uri.UnescapeDataString(headerValue).Trim();

        if (decoded.Contains("BEGIN CERTIFICATE", StringComparison.Ordinal))
            return X509Certificate2.CreateFromPem(decoded);

        var base64 = new string(decoded.Where(c => !char.IsWhiteSpace(c)).ToArray());
        return X509CertificateLoader.LoadCertificate(Convert.FromBase64String(base64));
    }
}
