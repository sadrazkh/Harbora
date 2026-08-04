using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Harbora.NodeAgent.Identity;
using Microsoft.Extensions.Logging;

namespace Harbora.NodeAgent.Transport;

/// <summary>
/// Builds the TLS settings every outbound connection uses: the node certificate going out, and the
/// rule for trusting what comes back.
///
/// <para>
/// Server trust is the system store first, then the CA the control plane handed over at
/// enrollment. Both are needed: a public panel behind Let's Encrypt validates against the system
/// store, and a self-hosted one behind a private CA validates against nothing else. Accepting
/// either keeps the private-CA case from being a reason to reach for "skip validation".
/// </para>
/// </summary>
public sealed class ControlPlaneTls(NodeAgentOptions options, ILogger<ControlPlaneTls> log)
{
    /// <summary>Client-side TLS options for a connection presenting <paramref name="identity"/>.</summary>
    public SslClientAuthenticationOptions Build(NodeIdentity? identity, string targetHost)
    {
        var ssl = new SslClientAuthenticationOptions
        {
            TargetHost = targetHost,
            RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
                ValidateServer(identity, certificate, chain, errors),
        };

        if (identity is not null)
            ssl.ClientCertificates = new X509CertificateCollection { identity.Certificate };

        return ssl;
    }

    /// <summary>An <see cref="HttpMessageHandler"/> wired with the same rules.</summary>
    public SocketsHttpHandler BuildHandler(NodeIdentity? identity)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
                    ValidateServer(identity, certificate, chain, errors),
            },
        };

        if (identity is not null)
            handler.SslOptions.ClientCertificates = new X509CertificateCollection { identity.Certificate };

        return handler;
    }

    private bool ValidateServer(
        NodeIdentity? identity,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        if (options.Security.TrustAnyControlPlaneCertificate)
        {
            // Loud on purpose. This is a development affordance and every connection made under it
            // should be visible in the journal as such.
            log.LogWarning("Control-plane certificate validation is disabled by configuration. Never do this in production.");
            return true;
        }

        if (errors == SslPolicyErrors.None) return true;

        // A name mismatch is never rescued by a private CA: the CA says who signed it, not who it
        // claims to be, and accepting a valid certificate for the wrong host is the whole attack.
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) ||
            errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            log.LogError("Control-plane certificate rejected: {Errors}.", errors);
            return false;
        }

        if (identity is null || certificate is null)
        {
            log.LogError("Control-plane certificate rejected ({Errors}) and no enrolled CA is available to fall back on.", errors);
            return false;
        }

        var chained = ChainsTo(certificate, identity.CaCertificate);
        if (!chained)
            log.LogError("Control-plane certificate rejected: {Errors}, and it does not chain to the enrolled CA.", errors);

        _ = chain; // The framework chain was built against the system store, which already failed.
        return chained;
    }

    /// <summary>True when the presented certificate chains to our enrolled CA, with no external trust required.</summary>
    internal static bool ChainsTo(X509Certificate presented, X509Certificate2 ca)
    {
        using var leaf = X509CertificateLoader.LoadCertificate(presented.Export(X509ContentType.Cert));
        using var chain = new X509Chain();

        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

        return chain.Build(leaf);
    }
}
